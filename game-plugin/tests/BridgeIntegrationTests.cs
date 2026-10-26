using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AmongLLM.Server.Hosting;
using AmongLLM.Server.Mcp;
using AmongLLM.Server.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AmongLLM.GamePlugin.Tests;

public sealed class BridgeIntegrationTests
{
    [Fact]
    public async Task RealPluginTransportRegistersWithHttpMcpAndExecutesRpcTool()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<GameInstanceRegistry>();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<GameTools>();
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/healthz", (HttpContext ctx) => Results.Json(new { service = "AmongLLM", protocolVersion = 1, serverId = ServiceIdentity.Id, port = ctx.Connection.LocalPort }));
        app.Map("/game/ws", (HttpContext ctx, GameInstanceRegistry registry) => registry.HandleSocketAsync(ctx));
        app.MapMcp("/mcp");
        await app.StartAsync();
        int port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        var observedCommand = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCommands = new ConcurrentQueue<JsonElement>();
        var plugin = new SocketClient((command, session) =>
        {
            if (command.GetProperty("type").GetString() != "command") return;
            observedCommands.Enqueue(command.Clone());
            observedCommand.TrySetResult(command.Clone());
            session.TrySend(new { type = "response", id = command.GetProperty("id").GetString(), ok = true, result = new { submitted = true } });
        }, _ => { }, new SocketClientOptions { Ports = new[] { port } });
        plugin.Start();
        try
        {
            var waitUntil = DateTime.UtcNow.AddSeconds(5);
            while (!plugin.IsActive && DateTime.UtcNow < waitUntil) await Task.Delay(10);
            Assert.True(plugin.IsActive);
            var instance = app.Services.GetRequiredService<GameInstanceRegistry>().GetInstance(SocketClient.InstanceId);
            Assert.Equal(Environment.ProcessId, instance.ProcessId);
            Assert.Equal(Environment.CurrentDirectory, instance.WorkingDirectory);
            plugin.CurrentSession!.TrySend(new { type = "heartbeat", instanceId = SocketClient.InstanceId, seq = 1, state = new { scene = "TestScene" } });
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            using var initResponse = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "AmongLLM integration tests", version = "1" } } });
            initResponse.EnsureSuccessStatusCode();
            if (initResponse.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) http.DefaultRequestHeaders.Add("Mcp-Session-Id", sessions.Single());
            using var init = ReadRpc(await initResponse.Content.ReadAsStringAsync());
            http.DefaultRequestHeaders.Add("MCP-Protocol-Version", init.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
            using var ready = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", method = "notifications/initialized" });
            ready.EnsureSuccessStatusCode();
            using var toolsResponse = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 2, method = "tools/list" });
            toolsResponse.EnsureSuccessStatusCode();
            using var tools = ReadRpc(await toolsResponse.Content.ReadAsStringAsync());
            string[] names = tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray();
            Assert.Contains("send_rpc", names);
            Assert.Contains("send_hazel_message", names);
            Assert.Contains("read_game_logs", names);
            foreach (var name in new[] { "list_game_servers", "set_game_server", "host_game", "join_game", "get_room_status" })
                Assert.Contains(name, names);
            using var callResponse = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "send_rpc", arguments = new { instanceId = SocketClient.InstanceId, targetNetId = 1, callId = 2, sendOption = "Reliable", targetClientId = -1, fields = Array.Empty<object>() } } });
            callResponse.EnsureSuccessStatusCode();
            using var result = ReadRpc(await callResponse.Content.ReadAsStringAsync());
            Assert.False(result.RootElement.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean());
            var observed = await observedCommand.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("send_rpc", observed.GetProperty("operation").GetString());
            Assert.Equal(1u, observed.GetProperty("payload").GetProperty("targetNetId").GetUInt32());
            var roomTools = new (string Name, object Arguments)[]
            {
                ("list_game_servers", new { instanceId = SocketClient.InstanceId }),
                ("set_game_server", new { instanceId = SocketClient.InstanceId, serverIndex = 2 }),
                ("host_game", new { instanceId = SocketClient.InstanceId }),
                ("join_game", new { instanceId = SocketClient.InstanceId, gameCode = "ABCDEF" }),
                ("get_room_status", new { instanceId = SocketClient.InstanceId })
            };
            foreach (var (name, arguments) in roomTools)
            {
                using var response = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = name, method = "tools/call", @params = new { name, arguments } });
                response.EnsureSuccessStatusCode();
                using var rpc = ReadRpc(await response.Content.ReadAsStringAsync());
                Assert.False(rpc.RootElement.GetProperty("result").TryGetProperty("isError", out var roomError) && roomError.GetBoolean());
            }
            var dispatched = observedCommands.ToArray();
            Assert.Equal(roomTools.Select(tool => tool.Name), dispatched.Skip(1).Select(command => command.GetProperty("operation").GetString()));
            Assert.Equal("Normal", dispatched[3].GetProperty("payload").GetProperty("gameMode").GetString());
        }
        finally
        {
            plugin.Stop();
            await plugin.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            await app.StopAsync();
        }
    }

    private static JsonDocument ReadRpc(string text)
    {
        if (text.TrimStart().StartsWith('{')) return JsonDocument.Parse(text);
        string data = text.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        return JsonDocument.Parse(data);
    }
}
