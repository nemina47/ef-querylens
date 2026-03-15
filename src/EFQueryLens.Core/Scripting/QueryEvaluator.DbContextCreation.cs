using System.Reflection;
using System.Runtime.Loader;

namespace EFQueryLens.Core.Scripting;

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

        var fromDesignTime = DesignTimeDbContextFactory.TryCreate(
            dbContextType,
            all,
            executableAssemblyPath,
            out var designTimeFailure);
        if (fromDesignTime is not null)
            return (fromDesignTime, "design-time-factory");

        var details = string.Join(" ", new[] { queryLensFailure, designTimeFailure }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        var executableHint = string.IsNullOrWhiteSpace(executableAssemblyPath)
            ? "Use the compiled executable assembly (API / Worker / Console) as the QueryLens target."
            : $"Selected executable assembly: '{Path.GetFileName(executableAssemblyPath)}'.";

        throw new InvalidOperationException(
            $"No factory found for '{dbContextType.FullName}'. " +
            "Add an IQueryLensDbContextFactory<T> implementation to your executable project (API / Worker / Console), not in a class library. " +
            executableHint + " " +
            "See the QueryLens README for setup instructions." +
            (string.IsNullOrWhiteSpace(details) ? string.Empty : $" Details: {details}"));
    }
}
