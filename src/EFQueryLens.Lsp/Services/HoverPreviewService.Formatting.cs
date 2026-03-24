using EFQueryLens.Core;
using SQL.Formatter;
using SQL.Formatter.Core;
using SQL.Formatter.Language;
using System.Text;
using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Lsp.Services;

internal sealed partial class HoverPreviewService
{
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

    private static string BuildHoverMarkdown(
        IReadOnlyList<QuerySqlCommand> commands,
        IReadOnlyList<QueryWarning> warnings,
        string uri,
        int line,
        int character,
        TranslationMetadata? metadata,
        double avgTranslationMs = 0,
        bool useBrowserSafeActionLinks = false)
    {
        var providerName = metadata?.ProviderName;
        var statements = BuildFormattedStatements(commands, providerName);
        var sql = BuildStatementsSqlBlock(statements);

        var statementWord = commands.Count == 1 ? "query" : "queries";
        var warningLines = BuildWarningLines(warnings);

        var queryParams = $"uri={Uri.EscapeDataString(uri)}&line={line}&character={character}";
        string copyUrl, openUrl, recalculateUrl;
        if (useBrowserSafeActionLinks)
        {
            copyUrl = $"efquerylens://copysql?{queryParams}";
            openUrl = $"efquerylens://opensqleditor?{queryParams}";
            recalculateUrl = $"efquerylens://recalculate?{queryParams}";
        }
        else
        {
            copyUrl = $"efquerylens://copySql?{queryParams}";
            openUrl = $"efquerylens://openSqlEditor?{queryParams}";
            recalculateUrl = $"efquerylens://recalculate?{queryParams}";
        }
        var copyLink = $"[Copy SQL]({copyUrl})";
        var openLink = $"[Open SQL]({openUrl})";
        var recalculateLink = $"[Reanalyze]({recalculateUrl})";

        // Plain Markdown only (no HTML entities) so VS Code, VS, and Rider all render the same.
        var header = $"**EF QueryLens** · {commands.Count} {statementWord}";
        var actionsRow = $"{copyLink} | {openLink} | {recalculateLink}";
        var timingLine = avgTranslationMs > 0
            ? $"\n\n*SQL generation time {avgTranslationMs:0} ms*"
            : string.Empty;

        var body = warningLines.Count == 0
            ? $"{header}  \n{actionsRow}\n\n```sql\n{sql}\n```{timingLine}"
            : $"{header}  \n{actionsRow}\n\n```sql\n{sql}\n```\n\n**Notes**\n{string.Join("\n", warningLines.Select(line => $"- {line}"))}{timingLine}";

        return body;
    }

    private static string? BuildStructuredEnrichedSql(
        string? rawSql,
        string sourceFile,
        int sourceLine,
        string? sourceExpression,
        string? dbContextType,
        string? providerName,
        IReadOnlyList<QueryWarning>? warnings = null)
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

        AppendCommentedExpression(sb, "LINQ", sourceExpression);

        if (!string.IsNullOrWhiteSpace(dbContextType))
        {
            sb.AppendLine($"-- DbContext: {dbContextType}");
        }

        if (!string.IsNullOrWhiteSpace(providerName))
        {
            sb.AppendLine($"-- Provider:  {providerName}");
        }

        if (warnings is { Count: > 0 })
        {
            sb.AppendLine("-- Notes:");
            foreach (var warning in warnings)
            {
                var warningLine = string.IsNullOrWhiteSpace(warning.Suggestion)
                    ? $"--   - {warning.Code}: {warning.Message}"
                    : $"--   - {warning.Code}: {warning.Message} ({warning.Suggestion})";
                sb.AppendLine(warningLine);
            }
        }

        sb.AppendLine();
        sb.Append(rawSql);
        return sb.ToString();
    }

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
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+FROM\s+", "\nFROM ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+WHERE\s+", "\nWHERE ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+GROUP BY\s+", "\nGROUP BY ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+ORDER BY\s+", "\nORDER BY ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+LEFT OUTER JOIN\s+", "\n  LEFT OUTER JOIN ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+RIGHT OUTER JOIN\s+", "\n  RIGHT OUTER JOIN ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+INNER JOIN\s+", "\n  INNER JOIN ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+JOIN\s+", "\n  JOIN ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return s.Trim();
        }
    }
}
