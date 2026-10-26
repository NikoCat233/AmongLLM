using System.Net;
using System.Net.Sockets;
using AmongLLM.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace mcp_server.Tests;

public sealed class PortBindingTests
{
    [Fact]
    public async Task StartsOnNextCandidateWhenPrimaryPortIsOccupied()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var primaryPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var fallbackPort = GetAvailablePort();
        while (fallbackPort == primaryPort)
            fallbackPort = GetAvailablePort();

        var bound = await PortBinding.StartFirstAvailableAsync(CreateApp, primaryPort, fallbackPort);
        try
        {
            Assert.Equal(fallbackPort, bound.Port);
            Assert.True(bound.App.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>() is not null);
        }
        finally
        {
            await bound.App.DisposeAsync();
        }
    }

    private static WebApplication CreateApp(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        return builder.Build();
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
