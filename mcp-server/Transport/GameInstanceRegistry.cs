using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using AmongLLM.Server.Hosting;
using ModelContextProtocol;

namespace AmongLLM.Server.Transport;

public sealed class GameInstanceRegistry(ILogger<GameInstanceRegistry> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan HeartbeatExpiry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ServerPingInterval = TimeSpan.FromSeconds(5);
    private const int MaxWebSocketMessageBytes = 32 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, GameConnection> _instances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _commandLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _registrationLock = new(1, 1);

    public IReadOnlyList<GameInstanceView> ListInstances()
    {
        var now = DateTimeOffset.UtcNow;
        return _instances.Values
            .Select(connection => connection.ToView(now, HeartbeatExpiry))
            .OrderBy(view => view.InstanceId, StringComparer.Ordinal)
            .ToArray();
    }

    public GameInstanceView GetInstance(string instanceId)
    {
        if (!_instances.TryGetValue(instanceId, out var connection))
            throw new McpException($"Game instance '{instanceId}' has not registered.");

        return connection.ToView(DateTimeOffset.UtcNow, HeartbeatExpiry);
    }

    public async Task<JsonElement> SendCommandAsync(
        string instanceId,
        string operation,
        object payload,
        TimeSpan timeout,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Command timeout must be positive.");
        if (!_instances.TryGetValue(instanceId, out var connection))
            throw new McpException($"Game instance '{instanceId}' has not registered.");

        var elapsed = Stopwatch.StartNew();
        var id = Guid.NewGuid().ToString("N");
        var payloadSummary = ServerLogSummary.SummarizePayload(operation, payload);
        using var commandScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["instanceId"] = connection.InstanceId,
            ["processId"] = connection.ProcessId,
            ["commandId"] = id,
            ["operation"] = operation
        });
        LogCommand(ServerLogEvents.CommandQueued, LogLevel.Debug, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
            "queued", "pending", payloadSummary);

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(timeout);
        var commandLock = _commandLocks.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
        try
        {
            await commandLock.WaitAsync(deadlineSource.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogCommand(ServerLogEvents.CommandCancelled, LogLevel.Debug, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
                "client_cancelled_before_send", "not_sent", payloadSummary);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogCommand(ServerLogEvents.CommandTimedOut, LogLevel.Warning, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
                "timeout_waiting_for_slot", "not_sent", payloadSummary);
            throw new TimeoutException($"Command '{operation}' timed out waiting for the game instance command slot; it was not sent.");
        }

        try
        {
            if (!_instances.TryGetValue(instanceId, out var current) || !ReferenceEquals(current, connection))
            {
                LogCommand(ServerLogEvents.CommandTransportError, LogLevel.Warning, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
                    "connection_replaced_before_send", "not_sent", payloadSummary, errorCode: "connection_replaced");
                throw new McpException($"Game instance '{instanceId}' reconnected before the command could be sent. Retry against the current connection.");
            }
            if (!connection.IsOnline(DateTimeOffset.UtcNow, HeartbeatExpiry))
            {
                LogCommand(ServerLogEvents.CommandTransportError, LogLevel.Warning, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
                    "offline_before_send", "not_sent", payloadSummary, errorCode: "instance_offline");
                throw new McpException($"Game instance '{instanceId}' is offline or its heartbeat expired.");
            }

            var pending = new PendingCommand(progress);
            if (!connection.Pending.TryAdd(id, pending))
                throw new InvalidOperationException("Generated duplicate command ID.");

            var mayHaveBeenSent = false;
            try
            {
                var command = new
                {
                    type = "command",
                    id,
                    operation,
                    payload = JsonSerializer.SerializeToElement(payload, Json),
                    deadlineUtc = deadline
                };

                mayHaveBeenSent = true;
                await connection.SendAsync(command, deadlineSource.Token);
                LogCommand(ServerLogEvents.CommandSent, LogLevel.Debug, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
                    "sent", "awaiting_response", payloadSummary);
                var response = await pending.Completion.Task.WaitAsync(deadlineSource.Token);

                if (!response.Ok)
                {
                    LogCommand(ServerLogEvents.CommandClientError, LogLevel.Warning, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
                        "client_error", ServerLogSummary.SanitizeText(response.Error), payloadSummary, response.ErrorCode);
                    throw new McpException(response.Error ?? $"Game command '{operation}' failed.");
                }

                var resultSummary = ServerLogSummary.SummarizeResult(operation, response.Result);
                LogCommand(ServerLogEvents.CommandCompleted, LogLevel.Information, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
                    "success", resultSummary, payloadSummary);
                return response.Result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var cancelSent = mayHaveBeenSent && await TrySendCancelAsync(connection, id, "client");
                LogCommand(ServerLogEvents.CommandCancelled, LogLevel.Debug, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
                    mayHaveBeenSent ? "client_cancelled_after_send" : "client_cancelled_before_send",
                    mayHaveBeenSent ? (cancelSent ? "cancel_sent; outcome_unknown" : "cancel_unavailable; outcome_unknown") : "not_sent",
                    payloadSummary);
                throw;
            }
            catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
            {
                var cancelSent = mayHaveBeenSent && await TrySendCancelAsync(connection, id, "timeout");
                LogCommand(ServerLogEvents.CommandTimedOut, LogLevel.Warning, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
                    mayHaveBeenSent ? "timeout_after_send" : "timeout_before_send",
                    mayHaveBeenSent ? (cancelSent ? "cancel_sent; outcome_unknown" : "cancel_unavailable; outcome_unknown") : "not_sent",
                    payloadSummary);
                throw new TimeoutException($"Command '{operation}' timed out. A cancel was sent when the connection allowed it; the outcome is unknown and it was not replayed.");
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception error)
            {
                LogCommand(ServerLogEvents.CommandTransportError, LogLevel.Error, connection, id, operation, elapsed.Elapsed.TotalMilliseconds,
                    "transport_error", "outcome_unknown", payloadSummary, errorType: error.GetType().Name);
                throw;
            }
            finally
            {
                connection.Pending.TryRemove(id, out _);
            }
        }
        finally
        {
            commandLock.Release();
        }
    }

    private void LogCommand(
        EventId eventId,
        LogLevel level,
        GameConnection connection,
        string commandId,
        string operation,
        double elapsedMs,
        string outcome,
        string resultSummary,
        string payloadSummary,
        string? errorCode = null,
        string? errorType = null)
    {
        var common = new object?[]
        {
            operation,
            outcome,
            connection.InstanceId,
            connection.ProcessId,
            commandId,
            Math.Round(elapsedMs, 3),
            ServerLogSummary.SanitizeText(resultSummary),
            ServerLogSummary.SanitizeText(payloadSummary)
        };
        if (errorCode is null && errorType is null)
        {
            logger.Log(level, eventId,
                "Game command {Operation}; outcome={Outcome}; instance={InstanceId} (PID {ProcessId}, command {CommandId}); {ElapsedMs} ms; result={ResultSummary}; payload={PayloadSummary}.",
                common);
            return;
        }

        var withError = new object?[common.Length + 2];
        Array.Copy(common, withError, common.Length);
        withError[^2] = ServerLogSummary.SanitizeText(errorCode);
        withError[^1] = ServerLogSummary.SanitizeText(errorType);
        logger.Log(level, eventId,
            "Game command {Operation}; outcome={Outcome}; instance={InstanceId} (PID {ProcessId}, command {CommandId}); {ElapsedMs} ms; result={ResultSummary}; payload={PayloadSummary}; errorCode={ErrorCode}; errorType={ErrorType}.",
            withError);
    }

    private async Task<bool> TrySendCancelAsync(GameConnection connection, string id, string reason)
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await connection.SendAsync(new { type = "cancel", id, reason }, bounded.Token);
            return true;
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async Task HandleSocketAsync(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var acceptedAt = Stopwatch.StartNew();
        logger.LogInformation(ServerLogEvents.WebSocketAccepted,
            "Accepted game WebSocket {ConnectionId} from {RemoteAddress}; serverId={ServerId}; elapsedMs={ElapsedMs}; outcome={Outcome}; resultSummary={ResultSummary}.",
            context.Connection.Id,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            ServiceIdentity.Id,
            0d,
            "accepted",
            "awaiting_registration");
        GameConnection? connection = null;
        try
        {
            using var first = await ReceiveDocumentAsync(socket, context.RequestAborted);
            if (first is null)
            {
                LogRegistrationRejected("unknown", 0, "missing_registration", acceptedAt.Elapsed.TotalMilliseconds);
                await RejectSocketAsync(socket, "invalid_registration", "A register message is required.");
                return;
            }
            if (!TryReadRegistration(first.RootElement, out var registration, out var error))
            {
                LogRegistrationRejected("unknown", 0, "invalid_registration", acceptedAt.Elapsed.TotalMilliseconds);
                await RejectSocketAsync(socket, "invalid_registration", error ?? "Invalid register message.");
                return;
            }

            var candidate = new GameConnection(registration!, socket);
            bool isReconnect;
            try
            {
                isReconnect = await RegisterAsync(candidate, context.RequestAborted);
            }
            catch (RegistrationRejectedException rejected)
            {
                LogRegistrationRejected(candidate.InstanceId, candidate.ProcessId, rejected.Code, acceptedAt.Elapsed.TotalMilliseconds);
                await RejectSocketAsync(socket, rejected.Code, rejected.Message);
                return;
            }

            connection = candidate;
            using var instanceScope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["instanceId"] = connection.InstanceId,
                ["processId"] = connection.ProcessId,
                ["workingDirectory"] = connection.WorkingDirectory
            });
            logger.LogInformation(
                isReconnect ? ServerLogEvents.GameInstanceReconnected : ServerLogEvents.GameInstanceRegistered,
                "Game instance {InstanceId} (PID {ProcessId}) {Outcome} after {ElapsedMs} ms; workingDirectory={WorkingDirectory}; resultSummary={ResultSummary}.",
                connection.InstanceId,
                connection.ProcessId,
                isReconnect ? "reconnected" : "registered",
                Math.Round(acceptedAt.Elapsed.TotalMilliseconds, 3),
                ServerLogSummary.SanitizeText(connection.WorkingDirectory),
                $"capabilities={connection.Capabilities.Length}; gameVersion={ServerLogSummary.SanitizeText(connection.GameVersion)}; modVersion={ServerLogSummary.SanitizeText(connection.ModVersion)}");
            await connection.SendAsync(new
            {
                type = "registered",
                serverId = ServiceIdentity.Id,
                instanceId = connection.InstanceId,
                heartbeatIntervalMs = 3000
            }, context.RequestAborted);

            using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var pingTask = SendPingsAsync(connection, connectionLifetime);
            try
            {
                while (socket.State == WebSocketState.Open && !connectionLifetime.IsCancellationRequested)
                {
                    using var document = await ReceiveDocumentAsync(socket, connectionLifetime.Token);
                    if (document is null)
                        break;

                    HandleClientMessage(connection, document.RootElement);
                }
            }
            catch (OperationCanceledException) when (connectionLifetime.IsCancellationRequested)
            {
            }
            finally
            {
                connectionLifetime.Cancel();
                try { await pingTask; }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            if (connection is null)
                logger.LogDebug(ServerLogEvents.WebSocketTransportError,
                    "Unregistered WebSocket closed; elapsedMs={ElapsedMs}; outcome={Outcome}; resultSummary={ResultSummary}.",
                    Math.Round(acceptedAt.Elapsed.TotalMilliseconds, 3), "disconnected_before_registration", "connection_closed");
        }
        catch (Exception error) when (connection is null && error is (InvalidDataException or JsonException))
        {
            LogRegistrationRejected("unknown", 0, "invalid_registration", acceptedAt.Elapsed.TotalMilliseconds);
            logger.LogDebug(ServerLogEvents.WebSocketTransportError,
                "Invalid registration frame; elapsedMs={ElapsedMs}; outcome={Outcome}; errorType={ErrorType}; resultSummary={ResultSummary}.",
                Math.Round(acceptedAt.Elapsed.TotalMilliseconds, 3), "registration_rejected", error.GetType().Name, "frame content omitted");
            await RejectSocketAsync(socket, "invalid_registration", "The registration frame was invalid.");
        }
        catch (Exception error)
        {
            logger.LogWarning(ServerLogEvents.WebSocketTransportError,
                "Game WebSocket failed; instanceId={InstanceId}; processId={ProcessId}; elapsedMs={ElapsedMs}; outcome={Outcome}; errorType={ErrorType}; resultSummary={ResultSummary}.",
                connection?.InstanceId ?? "unknown",
                connection?.ProcessId ?? 0,
                Math.Round(acceptedAt.Elapsed.TotalMilliseconds, 3),
                connection is null ? "registration_error" : "transport_error",
                error.GetType().Name,
                "message content omitted");
            await CloseAsync(socket, WebSocketCloseStatus.InvalidPayloadData, "Invalid protocol message.");
        }
        finally
        {
            if (connection is not null)
                await DisconnectAsync(connection);
        }
    }

    private async Task<bool> RegisterAsync(GameConnection connection, CancellationToken cancellationToken)
    {
        await _registrationLock.WaitAsync(cancellationToken);
        try
        {
            _instances.TryGetValue(connection.InstanceId, out var previous);
            if (previous is not null && previous.ProcessId != connection.ProcessId)
                throw new RegistrationRejectedException("instance_id_conflict", "This instanceId is already owned by a different game process.");

            _instances[connection.InstanceId] = connection;
            if (previous is not null)
            {
                previous.FailPending("A replacement connection registered for this instance.");
                await CloseAsync(previous.Socket, WebSocketCloseStatus.NormalClosure, "Replaced by reconnect.");
            }

            return previous is not null;
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    private async Task SendPingsAsync(GameConnection connection, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(ServerPingInterval, lifetime.Token);
                await connection.SendAsync(new { type = "ping", serverId = ServiceIdentity.Id, utc = DateTimeOffset.UtcNow }, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            logger.LogWarning(ServerLogEvents.WebSocketTransportError,
                "Game WebSocket ping failed; instanceId={InstanceId}; processId={ProcessId}; elapsedMs={ElapsedMs}; outcome={Outcome}; errorType={ErrorType}; resultSummary={ResultSummary}.",
                connection.InstanceId,
                connection.ProcessId,
                Math.Round((DateTimeOffset.UtcNow - connection.RegisteredAtUtc).TotalMilliseconds, 3),
                "ping_failed",
                error.GetType().Name,
                "ping payload omitted");
            lifetime.Cancel();
        }
    }

    private void LogRegistrationRejected(string instanceId, int processId, string errorCode, double elapsedMs) =>
        logger.LogWarning(ServerLogEvents.RegistrationRejected,
            "Game registration rejected; instanceId={InstanceId}; processId={ProcessId}; elapsedMs={ElapsedMs}; outcome={Outcome}; errorCode={ErrorCode}; resultSummary={ResultSummary}.",
            instanceId,
            processId,
            Math.Round(elapsedMs, 3),
            "rejected",
            ServerLogSummary.SanitizeText(errorCode),
            "registration metadata omitted");

    private static async Task RejectSocketAsync(WebSocket socket, string code, string message)
    {
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error = new { code, message } }, Json);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException)
            {
            }
        }
        await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "Registration rejected.");
    }

    private sealed class RegistrationRejectedException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private async Task DisconnectAsync(GameConnection connection)
    {
        if (_instances.TryGetValue(connection.InstanceId, out var current) && ReferenceEquals(current, connection))
        {
            var disconnectedAt = DateTimeOffset.UtcNow;
            connection.DisconnectedAtUtc = disconnectedAt;
            connection.FailPending("The game WebSocket disconnected.");
            logger.LogInformation(ServerLogEvents.GameInstanceDisconnected,
                "Game instance {InstanceId} (PID {ProcessId}) disconnected after {ElapsedMs} ms; outcome={Outcome}; resultSummary={ResultSummary}.",
                connection.InstanceId,
                connection.ProcessId,
                Math.Round((disconnectedAt - connection.RegisteredAtUtc).TotalMilliseconds, 3),
                "disconnected",
                "websocket closed; pending commands failed");
        }
    }

    private static void HandleClientMessage(GameConnection connection, JsonElement message)
    {
        if (!message.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Message type is missing.");

        var type = typeValue.GetString();
        if (type == "heartbeat")
        {
            var instanceId = message.GetProperty("instanceId").GetString();
            if (!string.Equals(instanceId, connection.InstanceId, StringComparison.Ordinal))
                throw new InvalidDataException("Heartbeat instance ID does not match the registered instance.");

            connection.LastHeartbeatUtc = DateTimeOffset.UtcNow;
            if (message.TryGetProperty("seq", out var sequence) && sequence.TryGetInt64(out var number))
                connection.LastSequence = Math.Max(connection.LastSequence, number);
            if (message.TryGetProperty("state", out var state))
                connection.State = state.Clone();
            return;
        }

        if (type == "pong")
            return;

        if (type is "response" or "progress")
        {
            if (!message.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Command correlation ID is missing.");

            var id = idElement.GetString()!;
            if (!connection.Pending.TryGetValue(id, out var pending))
                return;

            if (type == "progress")
            {
                pending.Report(message);
                return;
            }

            var ok = message.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            var result = message.TryGetProperty("result", out var resultElement)
                ? resultElement.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
            var error = message.TryGetProperty("error", out var errorElement)
                ? ReadCommandError(errorElement)
                : new GameCommandError(null, null);
            pending.Completion.TrySetResult(new GameCommandResponse(ok, result, error.Text, error.Code));
            return;
        }

        throw new InvalidDataException($"Unsupported client message type '{type}'.");
    }

    private static GameCommandError ReadCommandError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            var text = error.GetString();
            return new GameCommandError(text, null);
        }
        if (error.ValueKind != JsonValueKind.Object)
            return new GameCommandError(null, null);

        var code = error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
            ? codeValue.GetString()
            : null;
        var message = error.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
            ? messageValue.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(message))
            return new GameCommandError(
                string.IsNullOrWhiteSpace(code) ? "The game plugin reported a command error." : $"Game plugin error ({code}).",
                code);
        return new GameCommandError(string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}", code);
    }

    private static bool TryReadRegistration(JsonElement element, out GameRegistration? registration, out string? error)
    {
        registration = null;
        error = null;
        try
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != "register")
                throw new InvalidDataException("The first message must be register.");

            registration = element.Deserialize<GameRegistration>(Json)
                ?? throw new InvalidDataException("Registration message is empty.");
            if (registration.ProtocolVersion != 1)
                throw new InvalidDataException("Only protocol version 1 is supported.");
            if (registration.InstanceId is null || !Guid.TryParse(registration.InstanceId, out _))
                throw new InvalidDataException("instanceId must be a GUID.");
            if (registration.ProcessId <= 0)
                throw new InvalidDataException("processId must be positive.");
            if (string.IsNullOrWhiteSpace(registration.WorkingDirectory) || registration.WorkingDirectory.Length > 32768 || !Path.IsPathFullyQualified(registration.WorkingDirectory))
                throw new InvalidDataException("workingDirectory must be a fully qualified directory path.");
            if (registration.GameVersion is null || registration.ModVersion is null || registration.PlatformType is null ||
                registration.GameVersion.Length > 128 || registration.ModVersion.Length > 128 || registration.PlatformType.Length > 64)
                throw new InvalidDataException("Registration metadata exceeds the supported length.");
            if (registration.Capabilities is null || registration.Capabilities.Length > 128 || registration.Capabilities.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("capabilities must contain no more than 128 non-empty strings.");
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static async Task<JsonDocument?> ReceiveDocumentAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer, cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;
            if (received.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Only text WebSocket messages are accepted.");
            if (stream.Length + received.Count > MaxWebSocketMessageBytes)
                throw new InvalidDataException("WebSocket message exceeds the 32 MiB limit.");
            stream.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);

        return JsonDocument.Parse(stream.ToArray());
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseAsync(status, description, CancellationToken.None);
                else
                    await socket.CloseOutputAsync(status, description, CancellationToken.None);
            }
            catch (Exception error) when (error is WebSocketException or ObjectDisposedException)
            {
            }
        }
    }

    internal sealed class PendingCommand(IProgress<ProgressNotificationValue>? progress)
    {
        public TaskCompletionSource<GameCommandResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Report(JsonElement message)
        {
            if (progress is null)
                return;

            var value = message.TryGetProperty("progress", out var progressValue) ? progressValue : default;
            var amount = value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("completedSteps", out var completed) && completed.TryGetDouble(out var completedValue)
                    ? completedValue
                    : 0d;
            var total = message.TryGetProperty("total", out var totalValue) && totalValue.TryGetDouble(out var parsedTotal)
                ? parsedTotal
                : (double?)null;
            if (total is null && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("totalSteps", out var totalSteps) && totalSteps.TryGetDouble(out var parsedSteps))
                total = parsedSteps;
            var text = message.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
                ? messageValue.GetString()
                : value.ValueKind == JsonValueKind.Object && total is not null
                    ? $"Completed {amount:0}/{total:0} steps."
                    : "Game command is still running.";
            progress.Report(new ProgressNotificationValue { Progress = (float)amount, Total = total is null ? null : (float)total.Value, Message = text });
        }
    }

}

public sealed record GameRegistration
{
    public int ProtocolVersion { get; init; }
    public string InstanceId { get; init; } = "";
    public int ProcessId { get; init; }
    public string WorkingDirectory { get; init; } = "";
    public string GameVersion { get; init; } = "unknown";
    public string ModVersion { get; init; } = "unknown";
    public string PlatformType { get; init; } = "unknown";
    public string[] Capabilities { get; init; } = [];
}

public sealed record GameInstanceView(
    string InstanceId,
    int ProcessId,
    string WorkingDirectory,
    string GameVersion,
    string ModVersion,
    string PlatformType,
    string[] Capabilities,
    DateTimeOffset LastHeartbeatUtc,
    bool Online,
    long LastSequence,
    JsonElement State);

internal sealed record GameCommandResponse(bool Ok, JsonElement Result, string? Error, string? ErrorCode);
internal sealed record GameCommandError(string? Text, string? Code);

internal sealed class GameConnection(GameRegistration registration, WebSocket socket)
{
    public string InstanceId => registration.InstanceId;
    public int ProcessId => registration.ProcessId;
    public string WorkingDirectory => registration.WorkingDirectory;
    public string GameVersion => registration.GameVersion;
    public string ModVersion => registration.ModVersion;
    public string PlatformType => registration.PlatformType;
    public string[] Capabilities => registration.Capabilities;
    public WebSocket Socket { get; } = socket;
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public ConcurrentDictionary<string, GameInstanceRegistry.PendingCommand> Pending { get; } = new(StringComparer.Ordinal);
    public DateTimeOffset RegisteredAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastHeartbeatUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DisconnectedAtUtc { get; set; }
    public long LastSequence { get; set; }
    public JsonElement State { get; set; } = JsonDocument.Parse("{}").RootElement.Clone();

    public bool IsOnline(DateTimeOffset now, TimeSpan expiry) =>
        Socket.State == WebSocketState.Open && DisconnectedAtUtc is null && now - LastHeartbeatUtc <= expiry;

    public GameInstanceView ToView(DateTimeOffset now, TimeSpan expiry) => new(
        InstanceId, ProcessId, WorkingDirectory, GameVersion, ModVersion, PlatformType, Capabilities,
        LastHeartbeatUtc, IsOnline(now, expiry), LastSequence, State.Clone());

    public async Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await SendLock.WaitAsync(cancellationToken);
        try
        {
            if (Socket.State != WebSocketState.Open)
                throw new IOException("The game WebSocket is not open.");
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            SendLock.Release();
        }
    }

    public void FailPending(string message)
    {
        foreach (var pending in Pending.Values)
            pending.Completion.TrySetException(new IOException(message));
    }
}
