using AmongLLM.GamePlugin;
using System.Collections.Immutable;
using System.Reflection;

if (Environment.Version.Major != 6)
    throw new InvalidOperationException($"Smoke test requires .NET 6, got {Environment.Version}.");

// Match the host: load its framework copy before touching Roslyn. Merely
// testing a normal app with package DLLs can conceal the in-game conflict.
var immutable = typeof(ImmutableArray<>).Assembly;
var metadata = typeof(System.Reflection.Metadata.MetadataReader).Assembly;
if (immutable.GetName().Version!.Major != 6 || metadata.GetName().Version!.Major != 6)
    throw new InvalidOperationException("Smoke test must bind to framework Immutable/Metadata 6.");

if (args.Length != 1) throw new ArgumentException("Pass the published standalone plugin DLL path.");
var pluginPath = Path.GetFullPath(args[0]);
if (Directory.GetFiles(Path.GetDirectoryName(pluginPath)!, "*.dll").Length != 1)
    throw new InvalidOperationException("Smoke test requires a directory containing only the plugin DLL.");
var plugin = Assembly.LoadFrom(pluginPath);
if (plugin.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName != ".NETCoreApp,Version=v6.0")
    throw new InvalidOperationException("Published plugin must target .NET 6.");
var compile = plugin.GetType("AmongLLM.GamePlugin.ScriptCompiler", throwOnError: true)!
    .GetMethod("Compile", BindingFlags.Static | BindingFlags.Public)!;
Func<object, Task<object?>> Compile(string code, params string[] imports)
{
    return (Func<object, Task<object?>>)compile.Invoke(null,
        new object[] { code, typeof(SmokeGlobals), AppDomain.CurrentDomain.GetAssemblies(), imports })!;
}

var globals = new SmokeGlobals();
object? Run(string code)
{
    var runner = Compile(code,
        "System", "System.Linq", "System.Threading", "System.Threading.Tasks");
    return runner(globals).GetAwaiter().GetResult();
}

if (!Equals(Run("42"), 42)) throw new Exception("Expression result mismatch.");
if (!Equals(Run("Plugin.Value + Enumerable.Range(1, 3).Sum()"), 48)) throw new Exception("Globals/LINQ mismatch.");
if (!Equals(Run("typeof(Microsoft.CodeAnalysis.Compilation).Assembly.GetName().Version.ToString()"), "4.3.0.0"))
    throw new Exception("Embedded Roslyn metadata reference mismatch.");
using (var context = new UnityThreadContext(() => false))
{
    var runner = Compile("await Task.Delay(20); Thread.CurrentThread.ManagedThreadId", "System.Threading", "System.Threading.Tasks");
    int thread = Environment.CurrentManagedThreadId;
    var execution = context.Run(() => runner(globals));
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!execution.IsCompleted && DateTime.UtcNow < deadline) { context.Drain(); Thread.Sleep(1); }
    if (!execution.IsCompleted || !Equals(execution.GetAwaiter().GetResult(), thread))
        throw new Exception("Script await did not return to the calling thread.");
}
var roslyn = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name!.StartsWith("Microsoft.CodeAnalysis")).ToArray();
if (roslyn.Length != 4 || roslyn.Any(assembly => assembly.Location.Length != 0))
    throw new Exception("All four Roslyn assemblies must load from embedded resources.");
Console.WriteLine($"PASS: standalone plugin DLL; .NET {Environment.Version}; Immutable {immutable.GetName().Version}; Metadata {metadata.GetName().Version}; embedded Roslyn, expression, globals, LINQ, async continuation.");

public sealed class SmokeGlobals
{
    public SmokePlugin Plugin { get; } = new();
}
public sealed class SmokePlugin
{
    public int Value => 42;
}
