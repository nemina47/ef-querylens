using System.Reflection;
using System.Runtime.Loader;
using EFQueryLens.Core.Scripting.DesignTime;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    internal static (object Instance, string Strategy) CreateDbContextInstance(
        Type dbContextType,
        IEnumerable<Assembly> userAssemblies,
        string? executableAssemblyPath = null)
    {
        var all = AssemblyLoadContext
            .Default.Assemblies.Concat(userAssemblies)
            .ToList();

        var fromQueryLens = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            dbContextType,
            all,
            executableAssemblyPath,
            out var queryLensFailure);
        if (fromQueryLens is not null)
            return (fromQueryLens, "querylens-factory");

        var executableHint = string.IsNullOrWhiteSpace(executableAssemblyPath)
            ? "Use the compiled executable assembly (API / Worker / Console) as the QueryLens target."
            : $"Selected executable assembly: '{Path.GetFileName(executableAssemblyPath)}'.";

        throw new InvalidOperationException(
            $"No IQueryLensDbContextFactory<{dbContextType.Name}> found. " +
            "Add an IQueryLensDbContextFactory<T> implementation to your executable project (API / Worker / Console), not in a class library. " +
            executableHint +
            (string.IsNullOrWhiteSpace(queryLensFailure) ? string.Empty : $" Details: {queryLensFailure}"));
    }
}
