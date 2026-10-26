using System.Collections.Concurrent;
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

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(timeout);
        var commandLock = _commandLocks.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
        try
        {
            await commandLock.WaitAsync(deadlineSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Command '{operation}' timed out waiting for the game instance command slot; it was not sent.");
        }

        try
        {
            if (!_instances.TryGetValue(instanceId, out var current) || !ReferenceEquals(current, connection))
                throw new McpException($"Game instance '{instanceId}' reconnected before the command could be sent. Retry against the current connection.");
            if (!connection.IsOnline(DateTimeOffset.UtcNow, HeartbeatExpiry))
                throw new McpException($"Game instance '{instanceId}' is offline or its heartbeat expired.");

            var id = Guid.NewGuid().ToString("N");
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
                var response = await pending.Completion.Task.WaitAsync(deadlineSource.Token);

                if (!response.Ok)
                    throw new McpException(response.Error ?? $"Game command '{operation}' failed.");

                return response.Result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (mayHaveBeenSent)
                    await TrySendCancelAsync(connection, id, "client");
                throw;
            }
            catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
            {
                if (mayHaveBeenSent)
                    await TrySendCancelAsync(connection, id, "timeout");
                throw new TimeoutException($"Command '{operation}' timed out. A cancel was sent when the connection allowed it; the outcome is unknown and it was not replayed.");
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

    private async Task TrySendCancelAsync(GameConnection connection, string id, string reason)
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await connection.SendAsync(new { type = "cancel", id, reason }, bounded.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            logger.LogDebug("Could not send cancellation for a game command ({ErrorType}).", error.GetType().Name);
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
        GameConnection? connection = null;
        try
        {
            using var first = await ReceiveDocumentAsync(socket, context.RequestAborted);
            if (first is null)
            {
                await RejectSocketAsync(socket, "invalid_registration", "A register message is required.");
                return;
            }
            if (!TryReadRegistration(first.RootElement, out var registration, out var error))
            {
                await RejectSocketAsync(socket, "invalid_registration", error ?? "Invalid register message.");
                return;
            }

            var candidate = new GameConnection(registration!, socket);
            try
            {
                await RegisterAsync(candidate, context.RequestAborted);
            }
            catch (RegistrationRejectedException rejected)
            {
                await RejectSocketAsync(socket, rejected.Code, rejected.Message);
                return;
            }

            connection = candidate;
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
            logger.LogDebug("Game WebSocket disconnected.");
        }
        catch (Exception error) when (connection is null && error is (InvalidDataException or JsonException))
        {
            logger.LogDebug("Rejected game registration frame ({ErrorType}).", error.GetType().Name);
            await RejectSocketAsync(socket, "invalid_registration", "The registration frame was invalid.");
        }
        catch (Exception error)
        {
            logger.LogWarning("Rejected game WebSocket message ({ErrorType}).", error.GetType().Name);
            await CloseAsync(socket, WebSocketCloseStatus.InvalidPayloadData, "Invalid protocol message.");
        }
        finally
        {
            if (connection is not null)
                await DisconnectAsync(connection);
        }
    }

    private async Task RegisterAsync(GameConnection connection, CancellationToken cancellationToken)
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

            logger.LogInformation("Registered game instance {InstanceId} (PID {ProcessId}, game {GameVersion}, mod {ModVersion}).",
                connection.InstanceId, connection.ProcessId, connection.GameVersion, connection.ModVersion);
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
            logger.LogDebug("Game WebSocket ping failed ({ErrorType}).", error.GetType().Name);
            lifetime.Cancel();
        }
    }

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
            connection.DisconnectedAtUtc = DateTimeOffset.UtcNow;
            connection.FailPending("The game WebSocket disconnected.");
            logger.LogInformation("Game instance {InstanceId} disconnected.", connection.InstanceId);
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
                : null;
            pending.Completion.TrySetResult(new GameCommandResponse(ok, result, error));
            return;
        }

        throw new InvalidDataException($"Unsupported client message type '{type}'.");
    }

    private static string? ReadCommandError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString();
        if (error.ValueKind != JsonValueKind.Object)
            return null;

        var code = error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
            ? codeValue.GetString()
            : null;
        var message = error.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
            ? messageValue.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(message))
            return string.IsNullOrWhiteSpace(code) ? "The game plugin reported a command error." : $"Game plugin error ({code}).";
        return string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}";
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
    public string GameVersion { get; init; } = "unknown";
    public string ModVersion { get; init; } = "unknown";
    public string PlatformType { get; init; } = "unknown";
    public string[] Capabilities { get; init; } = [];
}

public sealed record GameInstanceView(
    string InstanceId,
    int ProcessId,
    string GameVersion,
    string ModVersion,
    string PlatformType,
    string[] Capabilities,
    DateTimeOffset LastHeartbeatUtc,
    bool Online,
    long LastSequence,
    JsonElement State);

internal sealed record GameCommandResponse(bool Ok, JsonElement Result, string? Error);

internal sealed class GameConnection(GameRegistration registration, WebSocket socket)
{
    public string InstanceId => registration.InstanceId;
    public int ProcessId => registration.ProcessId;
    public string GameVersion => registration.GameVersion;
    public string ModVersion => registration.ModVersion;
    public string PlatformType => registration.PlatformType;
    public string[] Capabilities => registration.Capabilities;
    public WebSocket Socket { get; } = socket;
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public ConcurrentDictionary<string, GameInstanceRegistry.PendingCommand> Pending { get; } = new(StringComparer.Ordinal);
    public DateTimeOffset LastHeartbeatUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DisconnectedAtUtc { get; set; }
    public long LastSequence { get; set; }
    public JsonElement State { get; set; } = JsonDocument.Parse("{}").RootElement.Clone();

    public bool IsOnline(DateTimeOffset now, TimeSpan expiry) =>
        Socket.State == WebSocketState.Open && DisconnectedAtUtc is null && now - LastHeartbeatUtc <= expiry;

    public GameInstanceView ToView(DateTimeOffset now, TimeSpan expiry) => new(
        InstanceId, ProcessId, GameVersion, ModVersion, PlatformType, Capabilities,
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
