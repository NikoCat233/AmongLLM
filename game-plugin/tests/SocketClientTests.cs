using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AmongLLM.GamePlugin.Tests;

public sealed class SocketClientTests
{
    [Fact]
    public void HealthProbeRequiresAmongLlmProtocolAndServerIdentity()
    {
        using JsonDocument valid = JsonDocument.Parse("""{"service":"AmongLLM","protocolVersion":1,"port":26767,"serverId":"server-a"}""");
        Assert.True(SocketClient.TryReadHealth(valid.RootElement, 26767, out string id, out _));
        Assert.Equal("server-a", id);

        using JsonDocument wrongService = JsonDocument.Parse("""{"service":"SomethingElse","protocolVersion":1,"port":26767,"serverId":"server-a"}""");
        using JsonDocument wrongProtocol = JsonDocument.Parse("""{"service":"AmongLLM","protocolVersion":2,"port":26767,"serverId":"server-a"}""");
        using JsonDocument wrongPort = JsonDocument.Parse("""{"service":"AmongLLM","protocolVersion":1,"port":26768,"serverId":"server-a"}""");
        using JsonDocument missingServer = JsonDocument.Parse("""{"service":"AmongLLM","protocolVersion":1,"port":26767}""");

        Assert.False(SocketClient.TryReadHealth(wrongService.RootElement, 26767, out _, out _));
        Assert.False(SocketClient.TryReadHealth(wrongProtocol.RootElement, 26767, out _, out _));
        Assert.False(SocketClient.TryReadHealth(wrongPort.RootElement, 26767, out _, out _));
        Assert.False(SocketClient.TryReadHealth(missingServer.RootElement, 26767, out _, out _));
    }

    [Fact]
    public async Task HealthCheckPrecedesWebSocketAndSessionIsPublishedOnlyAfterMatchingHandshake()
    {
        const int port = 26767;
        const string serverId = "server-a";
        var events = new List<string>();
        var handler = new CallbackHttpHandler((request, _) =>
        {
            events.Add("health");
            Assert.Equal($"http://127.0.0.1:{port}/healthz", request.RequestUri!.ToString());
            return Task.FromResult(JsonResponse($"{{\"service\":\"AmongLLM\",\"protocolVersion\":1,\"port\":{port},\"serverId\":\"{serverId}\"}}"));
        });
        using var http = new HttpClient(handler);
        var fakeSocket = new FakeWebSocket();
        var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new SocketClientOptions
        {
            Ports = new[] { port },
            HealthHttpClient = http,
            ConnectSocketAsync = (_, _) =>
            {
                events.Add("websocket");
                connectStarted.TrySetResult();
                return Task.FromResult<WebSocket>(fakeSocket);
            },
            RegistrationTimeout = TimeSpan.FromSeconds(3),
            ServerSilenceTimeout = TimeSpan.FromSeconds(30)
        };
        var client = new SocketClient((_, _) => { }, _ => { }, options);
        client.Start();

        await connectStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(client.CurrentSession);
        Assert.Equal(new[] { "health", "websocket" }, events);

        fakeSocket.EnqueueText($"{{\"type\":\"registered\",\"instanceId\":\"{SocketClient.InstanceId}\",\"serverId\":\"wrong-server\"}}");
        await Task.Delay(100);
        Assert.Null(client.CurrentSession);

        client.Stop();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task MatchingHandshakeActivatesSessionAndPingAndCancelReachExpectedHandlers()
    {
        using var parent = new CancellationTokenSource();
        var socket = new FakeWebSocket();
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SocketSession(socket, parent.Token, serverSilenceTimeout: TimeSpan.FromSeconds(10));
        socket.EnqueueText("{\"type\":\"registered\",\"instanceId\":\"instance-a\",\"serverId\":\"server-a\"}");

        await session.RegisterAsync("{}", "instance-a", "server-a", (message, _) => received.TrySetResult(message),
            TimeSpan.FromSeconds(2), parent.Token);
        Assert.True(session.IsActive);

        Task running = session.RunAsync();
        socket.EnqueueText("{\"type\":\"ping\"}");
        JsonElement pong = await socket.WaitForSentTypeAsync("pong").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("pong", pong.GetProperty("type").GetString());

        socket.EnqueueText("{\"type\":\"cancel\",\"id\":\"command-1\",\"reason\":\"timeout\"}");
        JsonElement cancel = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("cancel", cancel.GetProperty("type").GetString());
        Assert.Equal("command-1", cancel.GetProperty("id").GetString());

        session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(session.IsActive);
        session.Dispose();
    }

    [Fact]
    public async Task OutboundQueueOverflowCancelsSessionInsteadOfSilentlyDropping()
    {
        using var parent = new CancellationTokenSource();
        var socket = new FakeWebSocket { BlockNonRegistrationSends = true };
        var session = new SocketSession(socket, parent.Token, outboundCapacity: 1, maxQueuedBytes: 1024,
            serverSilenceTimeout: TimeSpan.FromSeconds(10));
        socket.EnqueueText("{\"type\":\"registered\",\"instanceId\":\"instance-a\",\"serverId\":\"server-a\"}");
        await session.RegisterAsync("{\"type\":\"register\"}", "instance-a", "server-a", (_, _) => { }, TimeSpan.FromSeconds(2), parent.Token);

        Task running = session.RunAsync();
        Assert.True(session.TrySend(new { type = "response", id = "1", result = "first" }));
        await socket.BlockedSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(session.TrySend(new { type = "response", id = "2", result = "queued" }));
        Assert.False(session.TrySend(new { type = "response", id = "3", result = "overflow" }));
        Assert.False(session.IsActive);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(2)));
        session.Dispose();
    }

    [Fact]
    public async Task ProbeRejectsWrongServiceWithoutAttemptingWebSocket()
    {
        int connectCount = 0;
        var handler = new CallbackHttpHandler((_, _) => Task.FromResult(JsonResponse("""{"service":"other","protocolVersion":1,"serverId":"server-a"}""")));
        using var http = new HttpClient(handler);
        var options = new SocketClientOptions
        {
            Ports = new[] { 26767 },
            HealthHttpClient = http,
            ConnectSocketAsync = (_, _) =>
            {
                Interlocked.Increment(ref connectCount);
                return Task.FromResult<WebSocket>(new FakeWebSocket());
            }
        };
        var client = new SocketClient((_, _) => { }, _ => { }, options);
        client.Start();
        await Task.Delay(100);
        Assert.Equal(0, connectCount);
        client.Stop();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class CallbackHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}

internal sealed class FakeWebSocket : WebSocket
{
    private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<JsonElement> sent = Channel.CreateUnbounded<JsonElement>();
    private WebSocketState state = WebSocketState.Open;
    public bool BlockNonRegistrationSends { get; init; }
    public TaskCompletionSource BlockedSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource releaseBlockedSend = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;

    public void EnqueueText(string text) => incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public async Task<JsonElement> WaitForSentTypeAsync(string type)
    {
        while (await sent.Reader.WaitToReadAsync())
        {
            while (sent.Reader.TryRead(out JsonElement message))
            {
                if (message.TryGetProperty("type", out JsonElement value) && value.GetString() == type) return message;
            }
        }
        throw new InvalidOperationException($"No outbound {type} message was received.");
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        byte[] message = await incoming.Reader.ReadAsync(cancellationToken);
        message.CopyTo(buffer.Array!, buffer.Offset);
        return new WebSocketReceiveResult(message.Length, WebSocketMessageType.Text, true);
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        byte[] copy = buffer.AsSpan().ToArray();
        using JsonDocument document = JsonDocument.Parse(copy);
        JsonElement message = document.RootElement.Clone();
        if (BlockNonRegistrationSends && (!message.TryGetProperty("type", out JsonElement type) || type.GetString() != "register"))
        {
            BlockedSendStarted.TrySetResult();
            await releaseBlockedSend.Task.WaitAsync(cancellationToken);
        }
        sent.Writer.TryWrite(message);
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        state = WebSocketState.Closed;
        incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort()
    {
        state = WebSocketState.Aborted;
        incoming.Writer.TryComplete();
        releaseBlockedSend.TrySetCanceled();
    }

    public override void Dispose()
    {
        state = WebSocketState.Closed;
        incoming.Writer.TryComplete();
        releaseBlockedSend.TrySetResult();
    }
}
