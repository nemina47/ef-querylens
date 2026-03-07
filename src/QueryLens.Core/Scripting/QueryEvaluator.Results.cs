using System.Reflection;

namespace QueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
    private static bool ShouldWarnExpressionPartialRisk(
        string expression,
        IReadOnlyList<QuerySqlCommand> commands)
    {
        if (commands.Count != 1 || string.IsNullOrWhiteSpace(expression))
            return false;

        var hasSelect = expression.Contains(".Select(", StringComparison.OrdinalIgnoreCase);
        if (!hasSelect)
            return false;

        var hasNestedMaterialization = expression.Contains(".ToList(", StringComparison.OrdinalIgnoreCase)
            || expression.Contains(".ToArray(", StringComparison.OrdinalIgnoreCase)
            || expression.Contains(".ToDictionary(", StringComparison.OrdinalIgnoreCase)
            || expression.Contains(".ToLookup(", StringComparison.OrdinalIgnoreCase);

        return hasNestedMaterialization;
    }

    private static void AddWarningIfMissing(List<QueryWarning> warnings, QueryWarning warning)
    {
        if (warnings.Any(w => string.Equals(w.Code, warning.Code, StringComparison.OrdinalIgnoreCase)))
            return;

        warnings.Add(warning);
    }

    private static QueryTranslationResult Failure(
        string message,
        TimeSpan elapsed,
        Type? dbContextType,
        IEnumerable<Assembly>? userAssemblies) =>
        new()
        {
            Success = false,
            ErrorMessage = message,
            Metadata = BuildMetadata(dbContextType, userAssemblies, elapsed),
        };

    private static TranslationMetadata BuildMetadata(
        Type? dbContextType,
        IEnumerable<Assembly>? userAssemblies,
        TimeSpan elapsed,
        string creationStrategy = "unknown") =>
        new()
        {
            DbContextType = dbContextType?.FullName ?? "unknown",
            ProviderName = userAssemblies is not null ? DetectProviderName(userAssemblies) : "unknown",
            EfCoreVersion = GetEfCoreVersion(userAssemblies),
            TranslationTime = elapsed,
            CreationStrategy = creationStrategy,
        };

    private static string DetectProviderName(IEnumerable<Assembly> assemblies)
    {
        foreach (var asm in assemblies)
        {
            var name = asm.GetName().Name;
            if (name is null)
                continue;

            if (name.StartsWith("Pomelo.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
                return "Pomelo.EntityFrameworkCore.MySql";
            if (name.StartsWith("Npgsql.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
                return "Npgsql.EntityFrameworkCore.PostgreSQL";
            if (name.Equals("Microsoft.EntityFrameworkCore.SqlServer", StringComparison.OrdinalIgnoreCase))
                return "Microsoft.EntityFrameworkCore.SqlServer";
            if (name.Equals("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase))
                return "Microsoft.EntityFrameworkCore.Sqlite";
            if (name.Equals("Microsoft.EntityFrameworkCore.InMemory", StringComparison.OrdinalIgnoreCase))
                return "Microsoft.EntityFrameworkCore.InMemory";
        }

        return "unknown";
    }

    private static string GetEfCoreVersion(IEnumerable<Assembly>? assemblies)
    {
        if (assemblies is null)
            return "unknown";

        return assemblies.FirstOrDefault(a => a.GetName().Name == "Microsoft.EntityFrameworkCore")
            ?.GetName().Version?.ToString() ?? "unknown";
    }
}
