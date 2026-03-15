using EFQueryLens.Core;
using SQL.Formatter;
using SQL.Formatter.Core;
using SQL.Formatter.Language;
using System.Text;

namespace EFQueryLens.Lsp.Services;

internal sealed partial class HoverPreviewService
{
    private static string BuildHoverMarkdown(
        IReadOnlyList<QuerySqlCommand> commands,
        IReadOnlyList<QueryWarning> warnings,
        string uri,
        int line,
        int character,
        TranslationMetadata? metadata)
    {
        var providerName = metadata?.ProviderName;
        var sql = string.Join(
            "\n\n",
            commands.Select((command, index) =>
            {
                var raw = commands.Count == 1
                    ? command.Sql.Trim()
                    : $"-- Split Query {index + 1} of {commands.Count}\n{command.Sql.Trim()}";
                return FormatSqlForDisplay(raw, providerName);
            }));

        var statementWord = commands.Count == 1 ? "query" : "queries";
        var warningLines = warnings
            .Select(w => string.IsNullOrWhiteSpace(w.Suggestion)
                ? $"- {w.Code}: {w.Message}"
                : $"- {w.Code}: {w.Message} ({w.Suggestion})")
            .ToArray();

        var queryParams = $"uri={Uri.EscapeDataString(uri)}&line={line}&character={character}";
        var copyLink = $"[Copy SQL](efquerylens://copySql?{queryParams})";
        var openLink = $"[Open SQL Editor](efquerylens://openSqlEditor?{queryParams})";
        var recalculateLink = $"[Recalculate](efquerylens://recalculate?{queryParams})";

        // Plain Markdown only (no HTML entities) so VS Code, VS, and Rider all render the same.
        var header = $"**QueryLens · {commands.Count} {statementWord}** | {copyLink} | {openLink} | {recalculateLink}";

        var body = warningLines.Length == 0
            ? $"{header}\n\n```sql\n{sql}\n```"
            : $"{header}\n\n```sql\n{sql}\n```\n\n**Notes**\n{string.Join("\n", warningLines)}";

        return body;
    }

    private static string? BuildStructuredEnrichedSql(
        string? rawSql,
        string sourceFile,
        int sourceLine,
        string? sourceExpression,
        string? dbContextType,
        string? providerName)
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
