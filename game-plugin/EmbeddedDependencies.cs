using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace AmongLLM.GamePlugin;

internal static class EmbeddedDependencies
{
    private const string Prefix = "AmongLLM.Dependencies.";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "Register embedded dependency resolution before the mod loader invokes plugin code.")]
    [ModuleInitializer]
    internal static void Initialize()
    {
        var context = AssemblyLoadContext.GetLoadContext(typeof(EmbeddedDependencies).Assembly)!;
        context.Resolving += Resolve;
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        using var stream = Open(name.Name!);
        return stream is null ? null : context.LoadFromStream(stream);
    }

    internal static Stream? Open(string assemblyName) =>
        typeof(EmbeddedDependencies).Assembly.GetManifestResourceStream(Prefix + assemblyName + ".dll");
}
