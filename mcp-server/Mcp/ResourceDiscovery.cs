using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace AmongLLM.Server.Mcp;

public static class ResourceDiscovery
{
    // Codex probes resource discovery even though all game capabilities are tools.
    public static IMcpServerBuilder WithEmptyResourceDiscovery(this IMcpServerBuilder builder) => builder
        .WithListResourcesHandler((_, _) => ValueTask.FromResult(new ListResourcesResult { Resources = [] }))
        .WithListResourceTemplatesHandler((_, _) => ValueTask.FromResult(new ListResourceTemplatesResult { ResourceTemplates = [] }));
}
