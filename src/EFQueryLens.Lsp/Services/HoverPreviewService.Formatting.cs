using EFQueryLens.Core;
using SQL.Formatter;
using SQL.Formatter.Core;
using SQL.Formatter.Language;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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

    [GeneratedRegex(@"^\s*//\s*var\s+.+:\s+.+\(used\s+\d+x\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVariableCommentRegex();

    private const int FormatterTimeoutMs = 2000;

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
        sb.AppendLine("# EF QueryLens");

        if (!string.IsNullOrWhiteSpace(sourceFile))
        {
            var lineDisplay = sourceLine > 0 ? $", line {sourceLine}" : string.Empty;
            sb.AppendLine($"- Source: `{sourceFile}{lineDisplay}`");
        }

        if (!string.IsNullOrWhiteSpace(efCoreVersion))
        {
            sb.AppendLine($"- EF Core: `{efCoreVersion}`");
        }

        if (!string.IsNullOrWhiteSpace(dbContextType))
        {
            sb.AppendLine($"- DbContext: `{ShortTypeName(dbContextType)}`");
        }

        if (!string.IsNullOrWhiteSpace(providerName))
        {
            sb.AppendLine($"- Provider: `{providerName}`");
        }

        AppendMarkdownExpression(sb, "LINQ", sourceExpression);

        // Only shown when EFQueryLens rewrote the expression before evaluation.
        if (!string.IsNullOrWhiteSpace(executedExpression)
            && !string.Equals(executedExpression, sourceExpression, StringComparison.Ordinal))
        {
            AppendMarkdownExpression(sb, "Executed LINQ", executedExpression);
        }

        if (parameters is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Parameters");
            var nameWidth = parameters.Max(p => p.Name.Length);
            foreach (var param in parameters)
            {
                var shortType = ShortTypeName(param.ClrType);
                var valueSuffix = string.IsNullOrWhiteSpace(param.InferredValue)
                    ? string.Empty
                    : $" = {param.InferredValue}";
                sb.AppendLine($"- `{param.Name.PadRight(nameWidth)}` `{shortType}`{valueSuffix}");
            }
        }

        var hasNotes = (warnings is { Count: > 0 }) || hasClientEvaluation;
        if (hasNotes)
        {
            sb.AppendLine();
            sb.AppendLine("## Notes");
            if (hasClientEvaluation)
            {
                sb.AppendLine("- Client evaluation: EF Core evaluated part of this query in memory (silent performance risk)");
            }

            if (warnings is { Count: > 0 })
            {
                foreach (var warning in warnings)
                {
                    var warningLine = string.IsNullOrWhiteSpace(warning.Suggestion)
                        ? $"- {warning.Code}: {warning.Message}"
                        : $"- {warning.Code}: {warning.Message} ({warning.Suggestion})";
                    sb.AppendLine(warningLine);
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("## SQL");
        sb.AppendLine("```sql");
        sb.AppendLine(rawSql.TrimEnd());
        sb.Append("```");
        return sb.ToString();
    }

    private static string ShortTypeName(string? fullName) =>
        fullName?.Split('.').LastOrDefault() ?? fullName ?? string.Empty;

    private static void AppendMarkdownExpression(StringBuilder sb, string label, string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"## {label} (csharp)");
        sb.AppendLine("```csharp");

        var formattedExpression = NormalizeCodeForComment(expression);
        foreach (var exprLine in formattedExpression.Replace("\r\n", "\n").Split('\n'))
        {
            sb.AppendLine(exprLine.TrimEnd());
        }

        sb.AppendLine("```");
    }

    private static string NormalizeCodeForComment(string expression)
    {
        var normalizedLineEndings = expression.Replace("\r\n", "\n");
        var cleanedExpression = RemoveSyntheticSemanticUsageComments(normalizedLineEndings);
        var looksLikeStatementSnippet = LooksLikeStatementSnippet(cleanedExpression);

        var csharpierFormatted = TryFormatSnippetWithCSharpier(cleanedExpression, looksLikeStatementSnippet);
        if (!string.IsNullOrWhiteSpace(csharpierFormatted))
        {
            return csharpierFormatted;
        }

        // Statement snippets can fail formatting when they don't compile in isolation.
        // Preserve authored layout in that case.
        if (looksLikeStatementSnippet)
        {
            return string.Join("\n", cleanedExpression.Split('\n').Select(line => line.TrimEnd()));
        }

        try
        {
            var parsed = SyntaxFactory.ParseExpression(cleanedExpression);
            if (parsed.ContainsDiagnostics)
            {
                return cleanedExpression;
            }

            return parsed.NormalizeWhitespace(indentation: "    ", eol: "\n").ToFullString();
        }
        catch
        {
            return cleanedExpression;
        }
    }

    private static string? TryFormatSnippetWithCSharpier(string code, bool statementSnippet)
    {
        const string beginMarker = "/*__QL_BEGIN__*/";
        const string endMarker = "/*__QL_END__*/";

        var wrappedCode = statementSnippet
            ? $$"""
            class __QlSnippetContainer
            {
                void __Run()
                {
                    {{beginMarker}}
            {{IndentBlock(code, 8)}}
                    {{endMarker}}
                }
            }
            """
            : $$"""
            class __QlSnippetContainer
            {
                void __Run()
                {
                    var __value = {{beginMarker}}
            {{IndentBlock(code, 8)}}
                    {{endMarker}};
                }
            }
            """;

        var formatterResult = TryFormatWithExternalFormatter(wrappedCode);
        if (formatterResult is null || !formatterResult.Success)
        {
            return null;
        }

        return ExtractBetweenMarkers(formatterResult.Code, beginMarker, endMarker);
    }

    private static string ExtractBetweenMarkers(string formattedCode, string beginMarker, string endMarker)
    {
        var begin = formattedCode.IndexOf(beginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return formattedCode.Trim();
        }

        begin += beginMarker.Length;
        var end = formattedCode.IndexOf(endMarker, begin, StringComparison.Ordinal);
        if (end < 0 || end < begin)
        {
            return formattedCode.Trim();
        }

        var snippet = formattedCode.Substring(begin, end - begin);
        return TrimIndentPreservingRelativeLayout(snippet);
    }

    private static string TrimIndentPreservingRelativeLayout(string value)
    {
        var normalized = value.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        var end = lines.Length - 1;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (end < start)
        {
            return string.Empty;
        }

        var relevant = lines.Skip(start).Take(end - start + 1).ToArray();
        var minIndent = relevant
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.TakeWhile(char.IsWhiteSpace).Count())
            .DefaultIfEmpty(0)
            .Min();

        for (var i = 0; i < relevant.Length; i++)
        {
            relevant[i] = relevant[i].Length >= minIndent ? relevant[i][minIndent..].TrimEnd() : relevant[i].TrimEnd();
        }

        return string.Join("\n", relevant);
    }

    private static string IndentBlock(string content, int indentSize)
    {
        var indent = new string(' ', indentSize);
        return string.Join("\n", content.Replace("\r\n", "\n").Split('\n').Select(line => $"{indent}{line}"));
    }

    private static ExternalFormatterResponse? TryFormatWithExternalFormatter(string code)
    {
        foreach (var processInfo in ResolveFormatterProcessStartInfos())
        {
            try
            {
                using var process = new Process { StartInfo = processInfo };
                if (!process.Start())
                {
                    continue;
                }

                var requestJson = JsonSerializer.Serialize(new ExternalFormatterRequest(code));
                process.StandardInput.Write(requestJson);
                process.StandardInput.Close();

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(FormatterTimeoutMs))
                {
                    TryKillProcess(process);
                    continue;
                }

                var stdout = stdoutTask.GetAwaiter().GetResult();
                _ = stderrTask.GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(stdout))
                {
                    continue;
                }

                var deserialized = JsonSerializer.Deserialize<ExternalFormatterResponse>(stdout);
                if (deserialized is not null)
                {
                    return deserialized;
                }
            }
            catch
            {
                // Try next formatter host strategy.
            }
        }

        return null;
    }

    private static IEnumerable<ProcessStartInfo> ResolveFormatterProcessStartInfos()
    {
        var isWindows = OperatingSystem.IsWindows();
        var exeName = isWindows ? "EFQueryLens.Formatter.exe" : "EFQueryLens.Formatter";
        var candidateDirectories = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "formatter"),
            AppContext.BaseDirectory,
        };

        foreach (var candidateDirectory in candidateDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exePath = Path.Combine(candidateDirectory, exeName);
            if (File.Exists(exePath))
            {
                yield return CreateFormatterProcessStartInfo(exePath, string.Empty);
            }

            var dllPath = Path.Combine(candidateDirectory, "EFQueryLens.Formatter.dll");
            if (File.Exists(dllPath))
            {
                yield return CreateFormatterProcessStartInfo("dotnet", $"\"{dllPath}\"");
            }
        }
    }

    private static ProcessStartInfo CreateFormatterProcessStartInfo(string fileName, string arguments)
    {
        return new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore kill errors.
        }
    }

    private sealed record ExternalFormatterRequest(string Code);

    private sealed record ExternalFormatterResponse(bool Success, string Code, IReadOnlyList<string>? Errors);

    private static string RemoveSyntheticSemanticUsageComments(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return code;
        }

        var lines = code.Split('\n');
        var filtered = lines.Where(line => !SemanticVariableCommentRegex().IsMatch(line)).ToArray();
        if (filtered.Length == lines.Length)
        {
            return code;
        }

        // Prevent an artificial leading blank block when only semantic comments were removed.
        var start = 0;
        while (start < filtered.Length && string.IsNullOrWhiteSpace(filtered[start]))
        {
            start++;
        }

        return string.Join("\n", filtered.Skip(start));
    }

    private static bool LooksLikeStatementSnippet(string code)
    {
        var trimmed = code.TrimStart();
        if (trimmed.StartsWith("var ", StringComparison.Ordinal)
            || trimmed.StartsWith("let ", StringComparison.Ordinal)
            || trimmed.StartsWith("query =", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return code.Contains(';');
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
