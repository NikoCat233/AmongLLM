using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AmongLLM.Server.Hosting;
using AmongLLM.Server.Mcp;
using AmongLLM.Server.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using Xunit;

namespace mcp_server.Tests;

public sealed class GameInstanceRegistryTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private GameInstanceRegistry _registry = null!;
    private Uri _webSocketUri = null!;
    private readonly RecordingLoggerProvider _logProvider = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logProvider);
        builder.Services.AddSingleton<GameInstanceRegistry>();
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/game/ws", (HttpContext context, GameInstanceRegistry registry) => registry.HandleSocketAsync(context));
        await _app.StartAsync();

        _registry = _app.Services.GetRequiredService<GameInstanceRegistry>();
        var addresses = _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses;
        var address = addresses.Single();
        var uriBuilder = new UriBuilder(address.Replace("http://", "ws://", StringComparison.Ordinal)) { Path = "/game/ws" };
        _webSocketUri = uriBuilder.Uri;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task RejectsRegistrationWithRelativeWorkingDirectory()
    {
        using var client = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await SendJsonAsync(client, new { type = "register", protocolVersion = 1, instanceId, processId = Environment.ProcessId, workingDirectory = "relative/game" });
        using var response = await ReceiveJsonAsync(client);
        Assert.Equal("error", response.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain(_registry.ListInstances(), instance => instance.InstanceId == instanceId);
    }

    [Fact]
    public async Task RegistersMultipleInstancesAndCorrelatesSequenceProgressAndResponse()
    {
        using var first = await ConnectAsync();
        using var second = await ConnectAsync();
        var firstId = Guid.NewGuid().ToString("D");
        var secondId = Guid.NewGuid().ToString("D");
        await RegisterAsync(first, firstId);
        await RegisterAsync(second, secondId);
        await SendJsonAsync(first, new { type = "heartbeat", instanceId = firstId, seq = 1, state = new { scene = "MainMenu", loginStatus = "LoggedIn" } });
        await SendJsonAsync(second, new { type = "heartbeat", instanceId = secondId, seq = 1, state = new { scene = "Lobby", isHost = true } });

        await WaitUntilAsync(() => _registry.ListInstances().Count == 2 && _registry.GetInstance(firstId).Online && _registry.GetInstance(secondId).Online);
        Assert.Equal(2, _registry.ListInstances().Count);
        Assert.Equal("MainMenu", _registry.GetInstance(firstId).State.GetProperty("scene").GetString());
        Assert.Equal(Environment.ProcessId, _registry.GetInstance(firstId).ProcessId);
        Assert.Equal(Environment.CurrentDirectory, _registry.GetInstance(firstId).WorkingDirectory);

        var progressSource = new TaskCompletionSource<ModelContextProtocol.ProgressNotificationValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<ModelContextProtocol.ProgressNotificationValue>(value => progressSource.TrySetResult(value));
        var dispatch = _registry.SendCommandAsync(firstId, "click_sequence", new
        {
            steps = JsonDocument.Parse("[{\"kind\":\"wait\",\"milliseconds\":20},{\"kind\":\"waitForButton\",\"timeoutMs\":2000}]").RootElement.Clone(),
            timeoutMs = 5000
        }, TimeSpan.FromSeconds(3), progress, CancellationToken.None);

        using var command = await ReceiveJsonAsync(first);
        Assert.Equal("command", command.RootElement.GetProperty("type").GetString());
        var commandId = command.RootElement.GetProperty("id").GetString();
        Assert.Equal("click_sequence", command.RootElement.GetProperty("operation").GetString());
        Assert.Equal(2, command.RootElement.GetProperty("payload").GetProperty("steps").GetArrayLength());
        await SendJsonAsync(first, new { type = "progress", id = commandId, progress = 1, total = 2, message = "wait step complete" });
        var reported = await progressSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1f, reported.Progress);
        Assert.Equal("wait step complete", reported.Message);
        await SendJsonAsync(first, new { type = "response", id = commandId, ok = true, result = new { stepsCompleted = 2 } });

        var result = await dispatch;
        Assert.Equal(2, result.GetProperty("stepsCompleted").GetInt32());
    }

    [Fact]
    public async Task TimedOutCommandIsNotReplayed()
    {
        using var client = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await RegisterAsync(client, instanceId);
        await SendJsonAsync(client, new { type = "heartbeat", instanceId, seq = 1, state = new { } });
        await WaitUntilAsync(() => _registry.GetInstance(instanceId).Online);

        var dispatch = _registry.SendCommandAsync(instanceId, "click_button", new { buttonId = "x" },
            TimeSpan.FromMilliseconds(250), null, CancellationToken.None);
        using var command = await ReceiveJsonAsync(client);
        Assert.Equal("click_button", command.RootElement.GetProperty("operation").GetString());
        var commandId = command.RootElement.GetProperty("id").GetString();
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => dispatch);
        Assert.Contains("outcome is unknown", timeout.Message, StringComparison.OrdinalIgnoreCase);

        using var cancel = await ReceiveJsonAsync(client, new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        Assert.Equal("cancel", cancel.RootElement.GetProperty("type").GetString());
        Assert.Equal(commandId, cancel.RootElement.GetProperty("id").GetString());
        Assert.Equal("timeout", cancel.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task CallerCancellationSendsCancelForPendingCommand()
    {
        using var client = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await RegisterAsync(client, instanceId);
        await SendJsonAsync(client, new { type = "heartbeat", instanceId, seq = 1, state = new { } });
        await WaitUntilAsync(() => _registry.GetInstance(instanceId).Online);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var dispatch = _registry.SendCommandAsync(instanceId, "click_sequence", new { steps = Array.Empty<object>() },
            TimeSpan.FromSeconds(5), null, cancellation.Token);
        using var command = await ReceiveJsonAsync(client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);

        using var cancel = await ReceiveJsonAsync(client, new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        Assert.Equal("cancel", cancel.RootElement.GetProperty("type").GetString());
        Assert.Equal(command.RootElement.GetProperty("id").GetString(), cancel.RootElement.GetProperty("id").GetString());
        Assert.Equal("client", cancel.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DeadlineIncludesTimeWaitingForPerInstanceCommandSlot()
    {
        using var client = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await RegisterAsync(client, instanceId);
        await SendJsonAsync(client, new { type = "heartbeat", instanceId, seq = 1, state = new { } });
        await WaitUntilAsync(() => _registry.GetInstance(instanceId).Online);

        var first = _registry.SendCommandAsync(instanceId, "click_button", new { buttonId = "first" },
            TimeSpan.FromSeconds(3), null, CancellationToken.None);
        using var firstCommand = await ReceiveJsonAsync(client);
        var queued = _registry.SendCommandAsync(instanceId, "click_button", new { buttonId = "queued" },
            TimeSpan.FromMilliseconds(250), null, CancellationToken.None);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => queued);
        Assert.Contains("waiting for the game instance command slot", timeout.Message, StringComparison.OrdinalIgnoreCase);

        var timeoutLog = _logProvider.Entries.Last(entry => entry.EventId == ServerLogEvents.CommandTimedOut &&
            Equals(entry.Values.GetValueOrDefault("Outcome"), "timeout_waiting_for_slot"));
        var queuedCommandId = timeoutLog.Values["CommandId"]?.ToString();
        Assert.NotNull(queuedCommandId);
        Assert.DoesNotContain(_logProvider.Entries, entry =>
            entry.EventId == ServerLogEvents.CommandSent && entry.Values["CommandId"]?.ToString() == queuedCommandId);

        await SendJsonAsync(client, new { type = "response", id = firstCommand.RootElement.GetProperty("id").GetString(), ok = true, result = new { done = true } });
        await first;
    }

    [Fact]
    public async Task SameInstanceReconnectCannotBeClaimedByDifferentProcess()
    {
        using var owner = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await RegisterAsync(owner, instanceId);
        await SendJsonAsync(owner, new { type = "heartbeat", instanceId, seq = 1, state = new { } });
        await WaitUntilAsync(() => _registry.GetInstance(instanceId).Online);

        using var impostor = await ConnectAsync();
        await SendJsonAsync(impostor, new
        {
            type = "register",
            protocolVersion = 1,
            instanceId,
            processId = Environment.ProcessId + 1,
            workingDirectory = Environment.CurrentDirectory,
            gameVersion = "test",
            modVersion = "test",
            platformType = "test",
            capabilities = Array.Empty<string>()
        });
        using var error = await ReceiveJsonAsync(impostor);
        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal("instance_id_conflict", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(Environment.ProcessId, _registry.GetInstance(instanceId).ProcessId);
    }

    [Fact]
    public async Task StaleConnectionDisconnectCannotTakeOwnershipFromSameProcessReconnect()
    {
        using var old = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await RegisterAsync(old, instanceId);
        await SendJsonAsync(old, new { type = "heartbeat", instanceId, seq = 1, state = new { marker = "old" } });
        await WaitUntilAsync(() => _registry.GetInstance(instanceId).Online);

        using var replacement = await ConnectAsync();
        await RegisterAsync(replacement, instanceId);
        await SendJsonAsync(replacement, new { type = "heartbeat", instanceId, seq = 1, state = new { marker = "new" } });
        await WaitUntilAsync(() =>
        {
            var state = _registry.GetInstance(instanceId).State;
            return state.ValueKind == JsonValueKind.Object && state.TryGetProperty("marker", out var marker) && marker.GetString() == "new";
        });

        old.Abort();
        await Task.Delay(50);
        var current = _registry.GetInstance(instanceId);
        Assert.True(current.Online);
        Assert.Equal("new", current.State.GetProperty("marker").GetString());
    }

    [Fact]
    public async Task ServerPingUsesStableServiceIdentity()
    {
        using var client = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await RegisterAsync(client, instanceId);

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        using var ping = await ReceiveJsonAsync(client, wait.Token);
        Assert.Equal("ping", ping.RootElement.GetProperty("type").GetString());
        Assert.Equal(ServiceIdentity.Id, ping.RootElement.GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task ScreenshotResponseCanExceedOneMiB()
    {
        using var client = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await RegisterAsync(client, instanceId);
        await SendJsonAsync(client, new { type = "heartbeat", instanceId, seq = 1, state = new { } });
        await WaitUntilAsync(() => _registry.GetInstance(instanceId).Online);

        var capture = GameTools.CaptureScreenshot(instanceId, _registry, CancellationToken.None);
        using var command = await ReceiveJsonAsync(client);
        var id = command.RootElement.GetProperty("id").GetString();
        var encoded = Convert.ToBase64String(new byte[1_100_000]);
        await SendJsonAsync(client, new { type = "response", id, ok = true, result = new { pngBase64 = encoded } });
        var result = await capture;
        Assert.NotEmpty(result.Content);
    }

    [Fact]
    public async Task PluginErrorObjectBecomesReadableMcpError()
    {
        using var client = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await RegisterAsync(client, instanceId);
        await SendJsonAsync(client, new { type = "heartbeat", instanceId, seq = 1, state = new { } });
        await WaitUntilAsync(() => _registry.GetInstance(instanceId).Online);

        var dispatch = GameTools.GetScreen(instanceId, _registry, CancellationToken.None);
        using var command = await ReceiveJsonAsync(client);
        await SendJsonAsync(client, new
        {
            type = "response",
            id = command.RootElement.GetProperty("id").GetString(),
            ok = false,
            error = new { code = "not_in_lobby", message = "A room is required. token=secret-do-not-log" }
        });
        var error = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => dispatch);
        Assert.Contains("not_in_lobby: A room is required. token=secret-do-not-log", error.Message, StringComparison.Ordinal);
        var clientErrorLog = Assert.Single(_logProvider.Entries, entry => entry.EventId == ServerLogEvents.CommandClientError);
        Assert.Contains("not_in_lobby", clientErrorLog.Values["ErrorCode"]?.ToString(), StringComparison.Ordinal);
        Assert.Contains("A room is required.", clientErrorLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-do-not-log", clientErrorLog.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", clientErrorLog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HazelEnvelopeAndDedicatedRpcUseExpectedPayloadShapes()
    {
        using var client = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await RegisterAsync(client, instanceId);
        await SendJsonAsync(client, new { type = "heartbeat", instanceId, seq = 1, state = new { } });
        await WaitUntilAsync(() => _registry.GetInstance(instanceId).Online);

        var message = JsonDocument.Parse("{\"mode\":\"raw\",\"sendOption\":\"Reliable\",\"fields\":[{\"type\":\"start\",\"tag\":5},{\"type\":\"int32\",\"value\":123},{\"type\":\"end\"}]}").RootElement.Clone();
        var rawDispatch = GameTools.SendHazelMessage(instanceId, message, _registry, CancellationToken.None);
        using (var command = await ReceiveJsonAsync(client))
        {
            Assert.Equal("send_hazel_message", command.RootElement.GetProperty("operation").GetString());
            Assert.Equal("raw", command.RootElement.GetProperty("payload").GetProperty("mode").GetString());
            Assert.Equal(123, command.RootElement.GetProperty("payload").GetProperty("fields")[1].GetProperty("value").GetInt32());
            await SendJsonAsync(client, new { type = "response", id = command.RootElement.GetProperty("id").GetString(), ok = true, result = new { submitted = true } });
        }
        await rawDispatch;

        var fields = JsonDocument.Parse("[{\"type\":\"packedUInt32\",\"value\":400}]").RootElement.Clone();
        var rpcDispatch = GameTools.SendRpc(instanceId, 17, 5, "Reliable", fields, _registry, CancellationToken.None);
        using (var command = await ReceiveJsonAsync(client))
        {
            var payload = command.RootElement.GetProperty("payload");
            Assert.Equal("send_rpc", command.RootElement.GetProperty("operation").GetString());
            Assert.Equal(17u, payload.GetProperty("targetNetId").GetUInt32());
            Assert.Equal(5, payload.GetProperty("callId").GetByte());
            Assert.Equal(-1, payload.GetProperty("targetClientId").GetInt32());
            await SendJsonAsync(client, new { type = "response", id = command.RootElement.GetProperty("id").GetString(), ok = true, result = new { submitted = true } });
        }
        await rpcDispatch;
        var rpcCompletion = Assert.Single(_logProvider.Entries, entry =>
            entry.EventId == ServerLogEvents.CommandCompleted && Equals(entry.Values.GetValueOrDefault("Operation"), "send_rpc"));
        Assert.Contains("targetNetId=17", rpcCompletion.Message, StringComparison.Ordinal);
        Assert.Contains("callId=5", rpcCompletion.Message, StringComparison.Ordinal);
        Assert.Contains("packedUInt32=400", rpcCompletion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SensitiveAndChatToolsUseStableProtocolFieldNames()
    {
        using var client = await ConnectAsync();
        var instanceId = Guid.NewGuid().ToString("D");
        await RegisterAsync(client, instanceId);
        await SendJsonAsync(client, new { type = "heartbeat", instanceId, seq = 1, state = new { } });
        await WaitUntilAsync(() => _registry.GetInstance(instanceId).Online);

        var chat = GameTools.SendChatMessage(instanceId, "hello bridge", _registry, CancellationToken.None);
        using (var command = await ReceiveJsonAsync(client))
        {
            var payload = command.RootElement.GetProperty("payload");
            Assert.Equal("send_chat_message", command.RootElement.GetProperty("operation").GetString());
            Assert.Equal("hello bridge", payload.GetProperty("message").GetString());
            await SendJsonAsync(client, new { type = "response", id = command.RootElement.GetProperty("id").GetString(), ok = true, result = new { sent = true } });
        }
        await chat;
        var chatEvents = _logProvider.Entries.Where(entry =>
            entry.Values.TryGetValue("Operation", out var operation) && Equals(operation, "send_chat_message")).ToArray();
        Assert.Contains(chatEvents, entry => entry.EventId == ServerLogEvents.CommandCompleted);
        Assert.DoesNotContain(chatEvents, entry =>
            (entry.EventId == ServerLogEvents.CommandQueued || entry.EventId == ServerLogEvents.CommandSent) && entry.Level >= LogLevel.Information);
        Assert.DoesNotContain(chatEvents.SelectMany(entry => entry.Values.Values).Select(value => value?.ToString() ?? ""),
            value => value.Contains("hello bridge", StringComparison.Ordinal));

        var logs = GameTools.ReadGameLogs(instanceId, _registry, CancellationToken.None,
            afterSequence: 42, limit: 50, sources: ["Unity"], levels: ["Warning"]);
        using (var command = await ReceiveJsonAsync(client))
        {
            var payload = command.RootElement.GetProperty("payload");
            Assert.Equal("read_game_logs", command.RootElement.GetProperty("operation").GetString());
            Assert.Equal(42, payload.GetProperty("afterSequence").GetInt64());
            Assert.Equal(50, payload.GetProperty("limit").GetInt32());
            Assert.Equal("Unity", payload.GetProperty("sources")[0].GetString());
            Assert.Equal("Warning", payload.GetProperty("levels")[0].GetString());
            await SendJsonAsync(client, new { type = "response", id = command.RootElement.GetProperty("id").GetString(), ok = true, result = new { entries = Array.Empty<object>(), nextSequence = 42 } });
        }
        await logs;

        var setText = GameTools.SetText(instanceId, "screen-1", 9876, "typed text", _registry, CancellationToken.None);
        using (var command = await ReceiveJsonAsync(client))
        {
            var payload = command.RootElement.GetProperty("payload");
            Assert.Equal("set_text", command.RootElement.GetProperty("operation").GetString());
            Assert.Equal("screen-1", payload.GetProperty("screenId").GetString());
            Assert.Equal(9876, payload.GetProperty("instanceId").GetInt32());
            Assert.Equal("typed text", payload.GetProperty("text").GetString());
            await SendJsonAsync(client, new { type = "response", id = command.RootElement.GetProperty("id").GetString(), ok = true, result = new { changed = true } });
        }
        await setText;

        var apiKey = "test-itch-key-never-log";
        var login = GameTools.SetItchAuthTokenAndLogin(instanceId, apiKey, _registry, CancellationToken.None);
        using (var command = await ReceiveJsonAsync(client))
        {
            var payload = command.RootElement.GetProperty("payload");
            Assert.Equal("set_itch_auth_token_and_login", command.RootElement.GetProperty("operation").GetString());
            Assert.Equal(apiKey, payload.GetProperty("platformAuthToken").GetString());
            Assert.False(payload.TryGetProperty("authToken", out _));
            await SendJsonAsync(client, new { type = "response", id = command.RootElement.GetProperty("id").GetString(), ok = true, result = new { loginStatus = "LoggedIn" } });
        }
        await login;
        var authLogText = string.Join("\n", _logProvider.Entries
            .Where(entry => entry.Values.TryGetValue("Operation", out var operation) && Equals(operation, "set_itch_auth_token_and_login"))
            .SelectMany(entry => entry.Values.Values.Select(value => value?.ToString() ?? "").Append(entry.Message)));
        Assert.DoesNotContain(apiKey, authLogText, StringComparison.Ordinal);
        Assert.DoesNotContain("platformAuthToken", authLogText, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(_webSocketUri, CancellationToken.None);
        return client;
    }

    private static async Task RegisterAsync(ClientWebSocket client, string instanceId)
    {
        await SendJsonAsync(client, new
        {
            type = "register",
            protocolVersion = 1,
            instanceId,
            processId = Environment.ProcessId,
            workingDirectory = Environment.CurrentDirectory,
            gameVersion = "test",
            modVersion = "test",
            platformType = "test",
            capabilities = new[] { "click_sequence" }
        });
        using var response = await ReceiveJsonAsync(client);
        Assert.Equal("registered", response.RootElement.GetProperty("type").GetString());
        Assert.Equal(instanceId, response.RootElement.GetProperty("instanceId").GetString());
    }

    private static async Task SendJsonAsync<T>(ClientWebSocket client, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await client.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket client, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        WebSocketReceiveResult received;
        do
        {
            received = await client.ReceiveAsync(buffer, cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Server closed the connection.");
            stream.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);
        return JsonDocument.Parse(stream.ToArray());
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < until)
            await Task.Delay(10);
        Assert.True(predicate(), "The expected registry state did not become available.");
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<RecordedLog> entries = new();
        public IReadOnlyList<RecordedLog> Entries => entries.ToArray();
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(entries);
        public void Dispose() { }

        private sealed class RecordingLogger(System.Collections.Concurrent.ConcurrentQueue<RecordedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> properties
                    ? properties.ToDictionary(pair => pair.Key, pair => pair.Value)
                    : new Dictionary<string, object?>();
                entries.Enqueue(new RecordedLog(logLevel, eventId, formatter(state, exception), values));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed record RecordedLog(LogLevel Level, EventId EventId, string Message, IReadOnlyDictionary<string, object?> Values);
}
