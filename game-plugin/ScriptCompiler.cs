using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace AmongLLM.GamePlugin;

internal static class ScriptCompiler
{
    // Keep this path shared with the .NET 6 runtime smoke test. A net10 test
    // host silently satisfies newer Roslyn framework dependencies.
    public static ScriptRunner<object?> Compile(string code, Type globalsType, IEnumerable<Assembly> assemblies, params string[] imports)
    {
        var references = assemblies
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
            .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(assembly.Location))
            .DistinctBy(reference => reference.Display, StringComparer.OrdinalIgnoreCase);
        var options = ScriptOptions.Default.WithReferences(references).WithImports(imports);
        return CSharpScript.Create<object?>(code, options, globalsType).CreateDelegate();
    }
}
