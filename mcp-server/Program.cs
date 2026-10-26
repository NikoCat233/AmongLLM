using System.Net;
using AmongLLM.Server.Hosting;
using AmongLLM.Server.Mcp;
using AmongLLM.Server.Transport;

const int primaryPort = 26767;
const int fallbackPort = 26768;

var bound = await PortBinding.StartFirstAvailableAsync(port => BuildApp(args, port), primaryPort, fallbackPort);

Console.WriteLine($"AmongLLM MCP server listening on http://127.0.0.1:{bound.Port}/mcp");
Console.WriteLine($"Game plugin WebSocket endpoint: ws://127.0.0.1:{bound.Port}/game/ws");
await bound.App.WaitForShutdownAsync();

static WebApplication BuildApp(string[] args, int port)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
    builder.Services.AddSingleton<GameInstanceRegistry>();
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithTools<GameTools>();

    var app = builder.Build();
    app.UseWebSockets();
    app.Use(async (context, next) =>
    {
        if (!IsLoopbackHost(context.Request.Host.Host) || !IsAllowedOrigin(context.Request.Headers.Origin.FirstOrDefault()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next();
    });
    app.MapGet("/healthz", () => Results.Json(new
    {
        service = ServiceIdentity.Name,
        serverId = ServiceIdentity.Id,
        protocolVersion = 1,
        processId = Environment.ProcessId,
        startedAtUtc = ServiceIdentity.StartedAtUtc,
        port
    }));
    app.Map("/game/ws", (HttpContext context, GameInstanceRegistry registry) => registry.HandleSocketAsync(context));
    app.MapMcp("/mcp");
    return app;
}

static bool IsLoopbackHost(string host) =>
    string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
    IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

static bool IsAllowedOrigin(string? origin)
{
    if (string.IsNullOrWhiteSpace(origin))
        return true;
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        return false;
    return IsLoopbackHost(uri.Host);
}
