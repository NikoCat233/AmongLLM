using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace AmongLLM.GamePlugin;

internal static class ScriptCompiler
{
    // Keep this path shared with the .NET 6 runtime smoke test. A net10 test
    // host silently satisfies newer Roslyn framework dependencies.
    // Keep Roslyn out of the entry point's signature and JIT body. The module
    // initializer must run before the runtime resolves any compiler types.
    public static Func<object, Task<object?>> Compile(string code, Type globalsType, IEnumerable<Assembly> assemblies, params string[] imports) =>
        CompileCore(code, globalsType, assemblies, imports);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<object, Task<object?>> CompileCore(string code, Type globalsType, IEnumerable<Assembly> assemblies, string[] imports)
    {
        var references = assemblies
            .Where(assembly => !assembly.IsDynamic)
            .DistinctBy(assembly => assembly.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(CreateReference)
            .OfType<MetadataReference>();
        var options = ScriptOptions.Default.WithReferences(references).WithImports(imports);
        var runner = CSharpScript.Create<object?>(code, options, globalsType).CreateDelegate();
        return globals => runner(globals);
    }

    private static MetadataReference? CreateReference(Assembly assembly)
    {
        if (!string.IsNullOrWhiteSpace(assembly.Location))
            return MetadataReference.CreateFromFile(assembly.Location);

        // Embedded Roslyn assemblies have no Location. Keep them usable from
        // execute_csharp by supplying their original PE metadata from resources.
        using var stream = EmbeddedDependencies.Open(assembly.GetName().Name!);
        return stream is null ? null : MetadataReference.CreateFromStream(stream);
    }
}
