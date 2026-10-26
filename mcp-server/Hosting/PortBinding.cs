using System.Net.Sockets;

namespace AmongLLM.Server.Hosting;

public sealed record BoundWebApplication(WebApplication App, int Port);

public static class PortBinding
{
    public static async Task<BoundWebApplication> StartFirstAvailableAsync(
        Func<int, WebApplication> appFactory,
        params int[] ports)
    {
        if (ports.Length == 0)
            throw new ArgumentException("At least one candidate port is required.", nameof(ports));

        foreach (var port in ports)
        {
            var candidate = appFactory(port);
            try
            {
                await candidate.StartAsync();
                return new BoundWebApplication(candidate, port);
            }
            catch (Exception error) when (IsAddressInUse(error))
            {
                await candidate.DisposeAsync();
                Console.Error.WriteLine($"Port {port} is occupied; trying the next configured port.");
            }
        }

        throw new InvalidOperationException($"Could not bind to any configured port: {string.Join(", ", ports)}.");
    }

    private static bool IsAddressInUse(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
                return true;
        return false;
    }
}
