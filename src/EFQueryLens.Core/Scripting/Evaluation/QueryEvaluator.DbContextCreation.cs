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

        var fromEfDesignTime = DesignTimeDbContextFactory.TryCreateEfDesignTimeFactory(
            dbContextType,
            all,
            executableAssemblyPath,
            out var efDesignTimeFailure);
        if (fromEfDesignTime is not null)
            return (fromEfDesignTime, "ef-design-time-factory");

        var executableHint = string.IsNullOrWhiteSpace(executableAssemblyPath)
            ? "Use the compiled executable assembly (API / Worker / Console) as the QueryLens target."
            : $"Selected executable assembly: '{Path.GetFileName(executableAssemblyPath)}'.";

        var details = string.Join(" ",
            new[] { queryLensFailure, efDesignTimeFailure }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        throw new InvalidOperationException(
            $"No IQueryLensDbContextFactory<{dbContextType.Name}> or IDesignTimeDbContextFactory<{dbContextType.Name}> found. " +
            "Add an IQueryLensDbContextFactory<T> or IDesignTimeDbContextFactory<T> implementation to your executable project (API / Worker / Console), not in a class library. " +
            executableHint +
            (string.IsNullOrWhiteSpace(details) ? string.Empty : $" Details: {details}"));
    }
}
