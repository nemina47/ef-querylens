using System.Reflection;
using System.Runtime.Loader;
using QueryLens.Core.AssemblyContext;

namespace QueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
    private static bool IsNoDbContextFoundError(InvalidOperationException ex) =>
        ex.Message.Contains("No DbContext subclass found", StringComparison.OrdinalIgnoreCase);

    private static void TryLoadSiblingAssemblies(ProjectAssemblyContext alcCtx)
    {
        var dir = Path.GetDirectoryName(alcCtx.AssemblyPath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;

        var loaded = alcCtx.LoadedAssemblies
            .Select(a => a.Location)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (loaded.Contains(dll))
                continue;

            try
            {
                alcCtx.LoadAdditionalAssembly(dll);
            }
            catch
            {
                // Best-effort dependency load to help DbContext discovery in sibling assemblies.
            }
        }
    }

    private static IReadOnlyList<Assembly> BuildCompilationAssemblySet(ProjectAssemblyContext alcCtx)
    {
        var userAssemblies = alcCtx.LoadedAssemblies.ToList();
        var userNames = userAssemblies
            .Select(a => a.GetName().Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.Ordinal);

        var merged = new List<Assembly>(userAssemblies);
        foreach (var asm in AssemblyLoadContext.Default.Assemblies)
        {
            var name = asm.GetName().Name;
            if (!string.IsNullOrWhiteSpace(name) && userNames.Contains(name))
                continue;

            merged.Add(asm);
        }

        return merged;
    }
}
