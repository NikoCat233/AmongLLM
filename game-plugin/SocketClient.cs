using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AmongLLM.GamePlugin;

internal sealed class SocketClient
{
    internal static readonly string InstanceId = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HttpClient DefaultHealthClient = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    private readonly Action<JsonElement, SocketSession> onMessage;
    private readonly Action<string> log;
    private readonly SocketClientOptions options;
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    private SocketSession? current;
    private int started;

    public SocketSession? CurrentSession => Volatile.Read(ref current);
    public bool IsActive => CurrentSession?.IsActive == true;
    internal Task Completion => Volatile.Read(ref worker) ?? Task.CompletedTask;

    /// <param name="log">
    /// The logger is invoked on the transport worker thread. The callback must enqueue
    /// messages for main-thread logging instead of calling Unity APIs directly.
    /// </param>
    public SocketClient(Action<JsonElement, SocketSession> onMessage, Action<string> log)
        : this(onMessage, log, new SocketClientOptions())
    {
    }

    internal SocketClient(Action<JsonElement, SocketSession> onMessage, Action<string> log, SocketClientOptions options)
    {
        this.onMessage = onMessage;
        this.log = log;
        this.options = options;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        worker = Task.Run(RunAsync);
    }

    public void Stop()
    {
        if (!stop.IsCancellationRequested) stop.Cancel();
        CurrentSession?.Cancel();
    }

    private async Task RunAsync()
    {
        int backoffSeconds = 1;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                bool handshakeSucceeded = false;
                foreach (int port in options.Ports)
                {
                    if (stop.IsCancellationRequested) return;
                    WebSocket? socket = null;
                    SocketSession? session = null;
                    try
                    {
                        string expectedServerId = await ProbeHealthAsync(port, stop.Token).ConfigureAwait(false);
                        socket = await ConnectSocketAsync(new Uri($"ws://127.0.0.1:{port}/game/ws"), stop.Token).ConfigureAwait(false);
                        session = new SocketSession(socket, stop.Token, options.OutboundCapacity, options.MaxQueuedBytes, options.ServerSilenceTimeout, options.SilencePollInterval);
                        await session.RegisterAsync(
                            CreateRegistrationMessage(), InstanceId, expectedServerId, onMessage, options.RegistrationTimeout, stop.Token).ConfigureAwait(false);

                        if (stop.IsCancellationRequested) return;
                        handshakeSucceeded = true;
                        backoffSeconds = 1;
                        Volatile.Write(ref current, session);
                        await session.RunAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception error)
                    {
                        ReportLog($"WebSocket connection to port {port} ended ({error.GetType().Name}).");
                    }
                    finally
                    {
                        if (session is not null)
                        {
                            Interlocked.CompareExchange(ref current, null, session);
                            session.Cancel();
                            session.Dispose();
                        }
                        else
                        {
                            socket?.Dispose();
                        }
                    }

                    // Once a server has completed registration, reconnect to the selected
                    // local service on the next pass instead of trying another process.
                    if (handshakeSucceeded || stop.IsCancellationRequested) break;
                }

                if (stop.IsCancellationRequested) return;
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stop.Token).ConfigureAwait(false);
                backoffSeconds = Math.Min(backoffSeconds * 2, 30);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private string CreateRegistrationMessage() => JsonSerializer.Serialize(new
    {
        type = "register",
        protocolVersion = 1,
        instanceId = InstanceId,
        processId = Environment.ProcessId,
        gameVersion = RuntimeInfo.GameVersion,
        modVersion = Plugin.PluginVersion,
        platformType = RuntimeInfo.PlatformType,
        capabilities = new[]
        {
            "capture_screenshot", "get_game_status", "get_screen", "click_button", "click_sequence",
            "get_account_info", "set_itch_auth_token_and_login", "send_chat_message", "send_hazel_message",
            "snap_to", "list_net_objects", "read_net_object_properties", "write_net_object_property",
            "execute_csharp", "list_loaded_mods", "send_rpc", "set_text"
        }
    }, Json);

    private async Task<string> ProbeHealthAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HealthTimeout);
        using HttpResponseMessage response = await (options.HealthHttpClient ?? DefaultHealthClient)
            .GetAsync($"http://127.0.0.1:{port}/healthz", HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Health probe returned HTTP {(int)response.StatusCode}.");

        byte[] body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (body.Length > 4096)
            throw new InvalidDataException("Health response exceeded the 4 KiB limit.");
        using JsonDocument document = JsonDocument.Parse(body);
        if (!TryReadHealth(document.RootElement, port, out string serverId, out string? error))
            throw new InvalidDataException(error ?? "Health response did not identify an AmongLLM protocol v1 server.");
        return serverId;
    }

    internal static bool TryReadHealth(JsonElement health, int expectedPort, out string serverId, out string? error)
    {
        serverId = "";
        error = null;
        if (health.ValueKind != JsonValueKind.Object ||
            !health.TryGetProperty("service", out JsonElement service) || service.ValueKind != JsonValueKind.String || service.GetString() != "AmongLLM")
        {
            error = "Health response service was not AmongLLM.";
            return false;
        }
        if (!health.TryGetProperty("protocolVersion", out JsonElement protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out int protocolVersion) || protocolVersion != 1)
        {
            error = "Health response protocolVersion was not 1.";
            return false;
        }
        if (health.TryGetProperty("port", out JsonElement advertisedPort) &&
            (advertisedPort.ValueKind != JsonValueKind.Number || !advertisedPort.TryGetInt32(out int parsedPort) || parsedPort != expectedPort))
        {
            error = "Health response port did not match the probed port.";
            return false;
        }
        if (!health.TryGetProperty("serverId", out JsonElement server) || server.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(server.GetString()))
        {
            error = "Health response did not include a serverId.";
            return false;
        }

        serverId = server.GetString()!;
        return true;
    }

    private Task<WebSocket> ConnectSocketAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (options.ConnectSocketAsync is not null)
            return options.ConnectSocketAsync(uri, cancellationToken);

        return ConnectClientWebSocketAsync(uri, cancellationToken);
    }

    private static async Task<WebSocket> ConnectClientWebSocketAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void ReportLog(string message)
    {
        try { log(message); }
        catch { }
    }
}

internal sealed class SocketClientOptions
{
    public IReadOnlyList<int> Ports { get; init; } = new[] { 26767, 26768 };
    public HttpClient? HealthHttpClient { get; init; }
    public Func<Uri, CancellationToken, Task<WebSocket>>? ConnectSocketAsync { get; init; }
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RegistrationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ServerSilenceTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan SilencePollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int OutboundCapacity { get; init; } = 128;
    public long MaxQueuedBytes { get; init; } = 64L * 1024 * 1024;
}

internal sealed class SocketSession : IDisposable
{
    private const int MaxIncomingMessageBytes = 1_048_576;
    private const int MaxOutgoingMessageBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly WebSocket socket;
    private readonly CancellationTokenSource lifetime;
    private readonly Channel<OutboundMessage> outbound;
    private readonly TaskCompletionSource<bool> registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan serverSilenceTimeout;
    private readonly TimeSpan silencePollInterval;
    private Action<JsonElement, SocketSession>? onMessage;
    private Task? receive;
    private long lastInboundTimestamp;
    private long queuedBytes;
    private int active;

    public bool IsActive => Volatile.Read(ref active) == 1 && !lifetime.IsCancellationRequested;

    internal SocketSession(WebSocket socket, CancellationToken parentToken, int outboundCapacity = 128,
        long maxQueuedBytes = 64L * 1024 * 1024, TimeSpan? serverSilenceTimeout = null, TimeSpan? silencePollInterval = null)
    {
        this.socket = socket;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        this.serverSilenceTimeout = serverSilenceTimeout ?? TimeSpan.FromSeconds(15);
        this.silencePollInterval = silencePollInterval ?? TimeSpan.FromSeconds(1);
        MaxQueuedBytes = maxQueuedBytes;
        outbound = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(outboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    private long MaxQueuedBytes { get; }

    public bool TrySend(object message)
    {
        if (!IsActive) return false;
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json); }
        catch
        {
            Cancel();
            return false;
        }

        if (bytes.Length > MaxOutgoingMessageBytes)
        {
            Cancel();
            return false;
        }

        long queued = Interlocked.Add(ref queuedBytes, bytes.Length);
        if (queued > MaxQueuedBytes)
        {
            Interlocked.Add(ref queuedBytes, -bytes.Length);
            Cancel();
            return false;
        }

        if (outbound.Writer.TryWrite(new OutboundMessage(bytes))) return true;
        Interlocked.Add(ref queuedBytes, -bytes.Length);
        Cancel();
        return false;
    }

    public async Task SendDirectAsync(string message, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length > MaxOutgoingMessageBytes)
            throw new InvalidDataException("Outgoing WebSocket message exceeded the 32 MiB limit.");
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    }

    public async Task RegisterAsync(string registrationMessage, string expectedInstanceId, string expectedServerId,
        Action<JsonElement, SocketSession> messageHandler, TimeSpan registrationTimeout, CancellationToken parentToken)
    {
        onMessage = messageHandler;
        receive = ReceiveLoopAsync(expectedInstanceId, expectedServerId, lifetime.Token);
        using var registration = CancellationTokenSource.CreateLinkedTokenSource(parentToken, lifetime.Token);
        registration.CancelAfter(registrationTimeout);
        try
        {
            await SendDirectAsync(registrationMessage, registration.Token).ConfigureAwait(false);
            await registered.Task.WaitAsync(registration.Token).ConfigureAwait(false);
        }
        catch
        {
            Cancel();
            try { await receive.ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async Task RunAsync()
    {
        if (!IsActive || receive is null) throw new InvalidOperationException("Session must complete registration before it is run.");
        Task send = SendLoopAsync(lifetime.Token);
        Task monitor = MonitorServerAsync(lifetime.Token);
        try
        {
            await Task.WhenAny(receive, send, monitor).ConfigureAwait(false);
            Cancel();
            await Task.WhenAll(receive, send, monitor).ConfigureAwait(false);
        }
        finally
        {
            Cancel();
        }
    }

    private async Task ReceiveLoopAsync(string expectedInstanceId, string expectedServerId, CancellationToken token)
    {
        byte[] buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Only text WebSocket messages are accepted.");
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxIncomingMessageBytes)
                    throw new InvalidDataException("WebSocket message exceeded the 1 MiB limit.");
                if (!result.EndOfMessage) continue;

                byte[] bytes = message.ToArray();
                message.SetLength(0);
                using JsonDocument document = JsonDocument.Parse(bytes);
                JsonElement root = document.RootElement.Clone();
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("WebSocket message must be a JSON object with a string type.");

                string messageType = type.GetString()!;
                if (!registered.Task.IsCompletedSuccessfully)
                {
                    if (messageType == "error")
                        throw new InvalidDataException(ReadServerError(root));
                    if (messageType != "registered")
                        throw new InvalidDataException("Server did not begin with a registered handshake response.");
                    ValidateRegisteredMessage(root, expectedInstanceId, expectedServerId);
                    Interlocked.Exchange(ref lastInboundTimestamp, Stopwatch.GetTimestamp());
                    Volatile.Write(ref active, 1);
                    registered.TrySetResult(true);
                    continue;
                }

                Interlocked.Exchange(ref lastInboundTimestamp, Stopwatch.GetTimestamp());
                if (messageType == "ping")
                {
                    if (!TrySend(new { type = "pong" })) return;
                    continue;
                }

                onMessage?.Invoke(root, this);
            }
        }
        catch (Exception error)
        {
            registered.TrySetException(error);
            throw;
        }
    }

    private void ValidateRegisteredMessage(JsonElement message, string expectedInstanceId, string expectedServerId)
    {
        if (!message.TryGetProperty("instanceId", out JsonElement instance) || instance.ValueKind != JsonValueKind.String || instance.GetString() != expectedInstanceId)
            throw new InvalidDataException("Registered handshake instanceId did not match this game process.");
        if (!message.TryGetProperty("serverId", out JsonElement server) || server.ValueKind != JsonValueKind.String || server.GetString() != expectedServerId)
            throw new InvalidDataException("Registered handshake serverId did not match the health probe.");
    }

    private static string ReadServerError(JsonElement message)
    {
        if (message.TryGetProperty("error", out JsonElement error))
        {
            if (error.ValueKind == JsonValueKind.String) return $"Server rejected registration: {error.GetString()}";
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                return $"Server rejected registration: {text.GetString()}";
        }
        return "Server rejected registration.";
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        await foreach (OutboundMessage message in outbound.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(message.Bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Add(ref queuedBytes, -message.Bytes.Length);
            }
        }
    }

    private async Task MonitorServerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(silencePollInterval, token).ConfigureAwait(false);
            long lastMessage = Volatile.Read(ref lastInboundTimestamp);
            if (lastMessage != 0 && TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - lastMessage) / (double)Stopwatch.Frequency) > serverSilenceTimeout)
                throw new TimeoutException("Server did not send a message or ping before the silence timeout.");
        }
    }

    public void Cancel()
    {
        Volatile.Write(ref active, 0);
        if (!lifetime.IsCancellationRequested) lifetime.Cancel();
        outbound.Writer.TryComplete();
    }

    public void Dispose()
    {
        Cancel();
        socket.Dispose();
        lifetime.Dispose();
    }

    private sealed record OutboundMessage(byte[] Bytes);
}
