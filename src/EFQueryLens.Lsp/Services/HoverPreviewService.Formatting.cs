using EFQueryLens.Core;
using SQL.Formatter;
using SQL.Formatter.Core;
using SQL.Formatter.Language;
using System.Text;
using System.Text.RegularExpressions;
using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Lsp.Services;

internal sealed partial class HoverPreviewService
{
    [GeneratedRegex(@"\s+FROM\s+", RegexOptions.IgnoreCase)]
    private static partial Regex FromClauseRegex();

    [GeneratedRegex(@"\s+WHERE\s+", RegexOptions.IgnoreCase)]
    private static partial Regex WhereClauseRegex();

    [GeneratedRegex(@"\s+GROUP BY\s+", RegexOptions.IgnoreCase)]
    private static partial Regex GroupByClauseRegex();

    [GeneratedRegex(@"\s+ORDER BY\s+", RegexOptions.IgnoreCase)]
    private static partial Regex OrderByClauseRegex();

    [GeneratedRegex(@"\s+LEFT OUTER JOIN\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeftOuterJoinRegex();

    [GeneratedRegex(@"\s+RIGHT OUTER JOIN\s+", RegexOptions.IgnoreCase)]
    private static partial Regex RightOuterJoinRegex();

    [GeneratedRegex(@"\s+INNER JOIN\s+", RegexOptions.IgnoreCase)]
    private static partial Regex InnerJoinRegex();

    [GeneratedRegex(@"\s+JOIN\s+", RegexOptions.IgnoreCase)]
    private static partial Regex JoinRegex();

    private static IReadOnlyList<QueryLensSqlStatement> BuildFormattedStatements(
        IReadOnlyList<QuerySqlCommand> commands,
        string? providerName)
    {
        return commands.Select((command, index) =>
        {
            var splitLabel = commands.Count > 1 ? $"Split Query {index + 1} of {commands.Count}" : null;
            var formatted = FormatSqlForDisplay(command.Sql.Trim(), providerName);
            return new QueryLensSqlStatement(formatted, splitLabel);
        }).ToList();
    }

    private static string BuildStatementsSqlBlock(IReadOnlyList<QueryLensSqlStatement> statements)
    {
        return string.Join(
            "\n\n",
            statements.Select(statement => string.IsNullOrWhiteSpace(statement.SplitLabel)
                ? statement.Sql
                : $"-- {statement.SplitLabel}\n{statement.Sql}"));
    }

    private static IReadOnlyList<string> BuildWarningLines(IReadOnlyList<QueryWarning> warnings)
    {
        return warnings.Select(warning => string.IsNullOrWhiteSpace(warning.Suggestion)
                ? $"{warning.Code}: {warning.Message}"
                : $"{warning.Code}: {warning.Message} ({warning.Suggestion})")
            .ToArray();
    }

    private string BuildHoverMarkdown(
        IReadOnlyList<QuerySqlCommand> commands,
        IReadOnlyList<QueryWarning> warnings,
        TranslationMetadata? metadata,
        double avgTranslationMs = 0,
        string? filePath = null,
        int line = 0,
        int character = 0)
    {
        var providerName = metadata?.ProviderName;
        var statements = BuildFormattedStatements(commands, providerName);
        var sql = BuildStatementsSqlBlock(statements);

        var statementWord = commands.Count == 1 ? "query" : "queries";
        var warningLines = BuildWarningLines(warnings);

        var isRiderClient = string.Equals(
            Environment.GetEnvironmentVariable("QUERYLENS_CLIENT"),
            "rider",
            StringComparison.OrdinalIgnoreCase);

        string? actionLinks = null;
        if (!isRiderClient && !string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                var fileUri = Uri.EscapeDataString(new Uri(filePath).AbsoluteUri);
                actionLinks =
                    $"[Copy SQL](efquerylens://copysql?uri={fileUri}&line={line}&character={character})" +
                    $" | [Open SQL](efquerylens://opensql?uri={fileUri}&line={line}&character={character})" +
                    $" | [Reanalyze](efquerylens://recalculate?uri={fileUri}&line={line}&character={character})";
            }
            catch { /* skip links if path is unparseable */ }
        }

        LogDebug(actionLinks is null
            ? $"hover-action-links omitted line={line} char={character}"
            : $"hover-action-links emitted line={line} char={character}");

        var header = actionLinks is null
            ? $"**EF QueryLens** · {commands.Count} {statementWord}"
            : $"**EF QueryLens** · {commands.Count} {statementWord} {actionLinks}";

        var riderHintLine = isRiderClient
            ? "\n\n*Actions available via Alt+Enter (EF QueryLens Actions...)*"
            : string.Empty;

        var timingLine = avgTranslationMs > 0
            ? $"\n\n*SQL generation time {avgTranslationMs:0} ms*"
            : string.Empty;

        var body = warningLines.Count == 0
            ? $"{header}\n\n```sql\n{sql}\n```{timingLine}{riderHintLine}"
            : $"{header}\n\n```sql\n{sql}\n```\n\n**Notes**\n{string.Join("\n", warningLines.Select(w => $"- {w}"))}{timingLine}{riderHintLine}";

        return body;
    }


    private static string? BuildStructuredEnrichedSql(
        string? rawSql,
        string sourceFile,
        int sourceLine,
        string? sourceExpression,
        string? executedExpression = null,
        string? efCoreVersion = null,
        string? dbContextType = null,
        string? providerName = null,
        IReadOnlyList<QueryWarning>? warnings = null,
        bool hasClientEvaluation = false,
        IReadOnlyList<QueryParameter>? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("-- EF QueryLens");

        if (!string.IsNullOrWhiteSpace(sourceFile))
        {
            var lineDisplay = sourceLine > 0 ? $", line {sourceLine}" : string.Empty;
            sb.AppendLine($"-- Source:    {sourceFile}{lineDisplay}");
        }

        if (!string.IsNullOrWhiteSpace(efCoreVersion))
        {
            sb.AppendLine($"-- EF Core:   {efCoreVersion}");
        }

        if (!string.IsNullOrWhiteSpace(dbContextType))
        {
            sb.AppendLine($"-- DbContext: {ShortTypeName(dbContextType)}");
        }

        if (!string.IsNullOrWhiteSpace(providerName))
        {
            sb.AppendLine($"-- Provider:  {providerName}");
        }

        AppendCommentedExpression(sb, "LINQ", sourceExpression);

        // Only shown when EFQueryLens rewrote the expression before evaluation.
        if (!string.IsNullOrWhiteSpace(executedExpression)
            && !string.Equals(executedExpression, sourceExpression, StringComparison.Ordinal))
        {
            AppendCommentedExpression(sb, "Executed LINQ", executedExpression);
        }

        if (parameters is { Count: > 0 })
        {
            sb.AppendLine("-- Parameters:");
            var nameWidth = parameters.Max(p => p.Name.Length);
            foreach (var param in parameters)
            {
                var shortType = ShortTypeName(param.ClrType);
                var valueSuffix = string.IsNullOrWhiteSpace(param.InferredValue)
                    ? string.Empty
                    : $" = {param.InferredValue}";
                sb.AppendLine($"--   {param.Name.PadRight(nameWidth)}  {shortType}{valueSuffix}");
            }
        }

        var hasNotes = (warnings is { Count: > 0 }) || hasClientEvaluation;
        if (hasNotes)
        {
            sb.AppendLine("-- Notes:");
            if (hasClientEvaluation)
            {
                sb.AppendLine("--   ⚠ Client evaluation: EF Core evaluated part of this query in memory (silent performance risk)");
            }

            if (warnings is { Count: > 0 })
            {
                foreach (var warning in warnings)
                {
                    var warningLine = string.IsNullOrWhiteSpace(warning.Suggestion)
                        ? $"--   - {warning.Code}: {warning.Message}"
                        : $"--   - {warning.Code}: {warning.Message} ({warning.Suggestion})";
                    sb.AppendLine(warningLine);
                }
            }
        }

        sb.AppendLine();
        sb.Append(rawSql);
        return sb.ToString();
    }

    private static string ShortTypeName(string? fullName) =>
        fullName?.Split('.').LastOrDefault() ?? fullName ?? string.Empty;

    private static void AppendCommentedExpression(StringBuilder sb, string label, string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return;
        }

        sb.AppendLine($"-- {label}:");
        foreach (var exprLine in expression.Replace("\r\n", "\n").Split('\n'))
        {
            sb.AppendLine(exprLine.Length == 0 ? "--" : $"--   {exprLine.TrimEnd()}");
        }
    }

    private static Dialect ResolveDialect(string? providerName) => providerName switch
    {
        { } p when p.Contains("MySql", StringComparison.OrdinalIgnoreCase)
                || p.Contains("MariaDb", StringComparison.OrdinalIgnoreCase) => Dialect.MySql,
        { } p when p.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
                || p.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase)
                || p.Contains("Postgres", StringComparison.OrdinalIgnoreCase) => Dialect.PostgreSql,
        { } p when p.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
                || p.Contains("SqlCe", StringComparison.OrdinalIgnoreCase) => Dialect.TSql,
        _ => Dialect.StandardSql,
    };

    private static string FormatSqlForDisplay(string sql, string? providerName = null)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;
        try
        {
            var dialect = ResolveDialect(providerName);
            return SqlFormatter.Of(dialect)
                .Format(sql, FormatConfig.Builder()
                    .Indent("  ")
                    .MaxColumnLength(120)
                    .Build());
        }
        catch
        {
            // Fallback: simple clause-break regex so hover still shows something readable.
            var s = sql.Trim();
            s = FromClauseRegex().Replace(s, "\nFROM ");
            s = WhereClauseRegex().Replace(s, "\nWHERE ");
            s = GroupByClauseRegex().Replace(s, "\nGROUP BY ");
            s = OrderByClauseRegex().Replace(s, "\nORDER BY ");
            s = LeftOuterJoinRegex().Replace(s, "\n  LEFT OUTER JOIN ");
            s = RightOuterJoinRegex().Replace(s, "\n  RIGHT OUTER JOIN ");
            s = InnerJoinRegex().Replace(s, "\n  INNER JOIN ");
            s = JoinRegex().Replace(s, "\n  JOIN ");
            return s.Trim();
        }
    }
}
