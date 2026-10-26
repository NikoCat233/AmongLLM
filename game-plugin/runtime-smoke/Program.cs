using AmongLLM.GamePlugin;
using System.Collections.Immutable;

if (Environment.Version.Major != 6)
    throw new InvalidOperationException($"Smoke test requires .NET 6, got {Environment.Version}.");

// Match the host: load its framework copy before touching Roslyn. Merely
// testing a normal app with package DLLs can conceal the in-game conflict.
var immutable = typeof(ImmutableArray<>).Assembly;
var metadata = typeof(System.Reflection.Metadata.MetadataReader).Assembly;
if (immutable.GetName().Version!.Major != 6 || metadata.GetName().Version!.Major != 6)
    throw new InvalidOperationException("Smoke test must bind to framework Immutable/Metadata 6.");

var globals = new SmokeGlobals();
object? Run(string code)
{
    var runner = ScriptCompiler.Compile(code, typeof(SmokeGlobals), AppDomain.CurrentDomain.GetAssemblies(),
        "System", "System.Linq", "System.Threading", "System.Threading.Tasks");
    return runner(globals).GetAwaiter().GetResult();
}

if (!Equals(Run("42"), 42)) throw new Exception("Expression result mismatch.");
if (!Equals(Run("Plugin.Value + Enumerable.Range(1, 3).Sum()"), 48)) throw new Exception("Globals/LINQ mismatch.");
using (var context = new UnityThreadContext(() => false))
{
    var runner = ScriptCompiler.Compile("await Task.Delay(20); Thread.CurrentThread.ManagedThreadId", typeof(SmokeGlobals),
        AppDomain.CurrentDomain.GetAssemblies(), "System.Threading", "System.Threading.Tasks");
    int thread = Environment.CurrentManagedThreadId;
    var execution = context.Run(() => runner(globals));
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!execution.IsCompleted && DateTime.UtcNow < deadline) { context.Drain(); Thread.Sleep(1); }
    if (!execution.IsCompleted || !Equals(execution.GetAwaiter().GetResult(), thread))
        throw new Exception("Script await did not return to the calling thread.");
}
Console.WriteLine($"PASS: .NET {Environment.Version}; Immutable {immutable.GetName().Version}; Metadata {metadata.GetName().Version}; expression, globals, LINQ, async continuation.");

public sealed class SmokeGlobals
{
    public SmokePlugin Plugin { get; } = new();
}
public sealed class SmokePlugin
{
    public int Value => 42;
}
