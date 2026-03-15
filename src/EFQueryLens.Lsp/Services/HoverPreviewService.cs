using EFQueryLens.Core;
using EFQueryLens.Lsp.Parsing;
using SQL.Formatter;
using SQL.Formatter.Core;
using SQL.Formatter.Language;
using System.Diagnostics;
using System.Text;

namespace EFQueryLens.Lsp.Services;

internal sealed record HoverPreviewComputationResult(
    bool Success,
    string Output,
    QueryTranslationStatus Status = QueryTranslationStatus.Ready,
    double AvgTranslationMs = 0);

internal sealed record QueryLensSqlStatement(string Sql, string? SplitLabel);

internal sealed record QueryLensStructuredHoverResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<QueryLensSqlStatement> Statements,
    int CommandCount,
    string? SourceExpression,
    string? DbContextType,
    string? ProviderName,
    string? SourceFile,
    int SourceLine,
    IReadOnlyList<string> Warnings,
    string? EnrichedSql,
    string? Mode,
    QueryTranslationStatus Status = QueryTranslationStatus.Ready,
    string? StatusMessage = null,
    double AvgTranslationMs = 0);

internal sealed class HoverPreviewService
{
    private readonly IQueryLensEngine _engine;

    public HoverPreviewService(IQueryLensEngine engine)
    {
        _engine = engine;
    }

    public async Task<HoverPreviewComputationResult> BuildMarkdownAsync(
        string filePath,
        string sourceText,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var expression = LspSyntaxHelper.TryExtractLinqExpression(sourceText, line, character, out var contextVariableName);
        Console.Error.WriteLine($"[QL-Hover] extract-linq line={line} char={character} found={!string.IsNullOrWhiteSpace(expression)} ctx={contextVariableName}");

        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(contextVariableName))
        {
            return new HoverPreviewComputationResult(false, "Could not extract a LINQ query expression at the current caret location.");
        }

        var targetAssembly = AssemblyResolver.TryGetTargetAssembly(filePath);
        if (string.IsNullOrWhiteSpace(targetAssembly) ||
            targetAssembly.StartsWith("DEBUG_FAIL", StringComparison.Ordinal) ||
            !File.Exists(targetAssembly))
        {
            return new HoverPreviewComputationResult(false, "Could not locate compiled target assembly for this file. Build the project and try again.");
        }

        var usingContext = LspSyntaxHelper.ExtractUsingContext(sourceText);

        try
        {
            var sw = Stopwatch.StartNew();
            Console.Error.WriteLine($"[QL-Hover] translate-start line={line} char={character} assembly={targetAssembly}");

            var queued = await _engine.TranslateQueuedAsync(new TranslationRequest
            {
                AssemblyPath = targetAssembly,
                Expression = expression,
                ContextVariableName = contextVariableName,
                AdditionalImports = usingContext.Imports.ToArray(),
                UsingAliases = new Dictionary<string, string>(usingContext.Aliases, StringComparer.Ordinal),
                UsingStaticTypes = usingContext.StaticTypes.ToArray(),
            }, cancellationToken);

            if (queued.Status is not QueryTranslationStatus.Ready)
            {
                sw.Stop();
                var statusMessage = queued.Status switch
                {
                    QueryTranslationStatus.Starting => "EF QueryLens is starting up and warming the translation pipeline.",
                    QueryTranslationStatus.InQueue => "EF QueryLens queued this query and is still processing it.",
                    QueryTranslationStatus.Unreachable => "EF QueryLens services are unavailable. Could not communicate with daemon.",
                    _ => "EF QueryLens is processing this query.",
                };

                Console.Error.WriteLine(
                    $"[QL-Hover] queued-status line={line} char={character} " +
                    $"status={queued.Status} avgMs={queued.AverageTranslationMs:0.##}");

                return new HoverPreviewComputationResult(
                    Success: true,
                    Output: BuildQueuedStatusMarkdown(queued.Status, statusMessage, queued.AverageTranslationMs),
                    Status: queued.Status,
                    AvgTranslationMs: queued.AverageTranslationMs);
            }

            var translation = queued.Result;
            if (translation is null)
            {
                sw.Stop();
                Console.Error.WriteLine($"[QL-Hover] translate-missing-result line={line} char={character}");
                return new HoverPreviewComputationResult(false, "Queued translation completed without a result payload.");
            }

            sw.Stop();
            Console.Error.WriteLine($"[QL-Hover] translate-finished line={line} char={character} success={translation.Success} elapsedMs={sw.ElapsedMilliseconds} commands={translation.Commands.Count} sqlLen={(translation.Sql?.Length ?? 0)}");

            if (!translation.Success)
            {
                Console.Error.WriteLine($"[QL-Hover] translate-error line={line} char={character} message={translation.ErrorMessage}");
                return new HoverPreviewComputationResult(false, translation.ErrorMessage ?? "Translation failed.");
            }

            var commands = translation.Commands.Count > 0
                ? translation.Commands
                : translation.Sql is null
                    ? []
                    : [new QuerySqlCommand { Sql = translation.Sql, Parameters = translation.Parameters }];

            if (commands.Count == 0)
            {
                Console.Error.WriteLine($"[QL-Hover] translate-empty-commands line={line} char={character}");
                return new HoverPreviewComputationResult(false, "No SQL was produced for this expression.");
            }

            var documentUri = DocumentPathResolver.ToUri(filePath);
            var metadata = translation.Metadata;
            var markdown = BuildHoverMarkdown(
                commands, translation.Warnings, documentUri, line, character, metadata);
            Console.Error.WriteLine($"[QL-Hover] hover-markdown-ready line={line} char={character} markdownLen={markdown.Length}");
            return new HoverPreviewComputationResult(true, markdown, QueryTranslationStatus.Ready, queued.AverageTranslationMs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[QL-Hover] translate-exception line={line} char={character} type={ex.GetType().Name} message={ex.Message}");
            return new HoverPreviewComputationResult(false, $"{ex.GetType().Name}: {ex.Message}", QueryTranslationStatus.Unreachable);
        }
    }

    private static string BuildQueuedStatusMarkdown(
        QueryTranslationStatus status,
        string statusMessage,
        double avgTranslationMs)
    {
        var statusLabel = status switch
        {
            QueryTranslationStatus.Starting => "🟠 starting",
            QueryTranslationStatus.InQueue => "🔵 queued",
            QueryTranslationStatus.Unreachable => "🔴 unavailable",
            _ => "🟢 ready",
        };

        var avgLine = avgTranslationMs > 0
            ? $"\n\nAverage SQL generation time: {avgTranslationMs:0} ms."
            : string.Empty;

        return $"**QueryLens · {statusLabel}**\n\n{statusMessage}{avgLine}";
    }

    public async Task<QueryLensStructuredHoverResult> BuildStructuredAsync(
        string filePath,
        string sourceText,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        static QueryLensStructuredHoverResult Fail(
            string msg,
            QueryTranslationStatus status = QueryTranslationStatus.Ready,
            double avgTranslationMs = 0) =>
            new(false, msg, [], 0, null, null, null, null, 0, [], null, null, status, msg, avgTranslationMs);

        var expression = LspSyntaxHelper.TryExtractLinqExpression(sourceText, line, character, out var contextVariableName);
        Console.Error.WriteLine($"[QL-Hover] structured extract-linq line={line} char={character} found={!string.IsNullOrWhiteSpace(expression)} ctx={contextVariableName}");

        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(contextVariableName))
        {
            return Fail("Could not extract a LINQ query expression at the current caret location.");
        }

        var targetAssembly = AssemblyResolver.TryGetTargetAssembly(filePath);
        if (string.IsNullOrWhiteSpace(targetAssembly) ||
            targetAssembly.StartsWith("DEBUG_FAIL", StringComparison.Ordinal) ||
            !File.Exists(targetAssembly))
        {
            return Fail("Could not locate compiled target assembly for this file. Build the project and try again.");
        }

        var usingContext = LspSyntaxHelper.ExtractUsingContext(sourceText);

        try
        {
            var sw = Stopwatch.StartNew();
            Console.Error.WriteLine($"[QL-Hover] structured translate-start line={line} char={character} assembly={targetAssembly}");

            var queued = await _engine.TranslateQueuedAsync(new TranslationRequest
            {
                AssemblyPath = targetAssembly,
                Expression = expression,
                ContextVariableName = contextVariableName,
                AdditionalImports = usingContext.Imports.ToArray(),
                UsingAliases = new Dictionary<string, string>(usingContext.Aliases, StringComparer.Ordinal),
                UsingStaticTypes = usingContext.StaticTypes.ToArray(),
            }, cancellationToken);

            if (queued.Status is not QueryTranslationStatus.Ready)
            {
                sw.Stop();
                var statusMessage = queued.Status switch
                {
                    QueryTranslationStatus.Starting => "EF QueryLens is starting up and warming the translation pipeline.",
                    QueryTranslationStatus.InQueue => "EF QueryLens queued this query and is still processing it.",
                    QueryTranslationStatus.Unreachable => "EF QueryLens services are unavailable. Could not communicate with daemon.",
                    _ => "EF QueryLens is processing this query.",
                };

                Console.Error.WriteLine(
                    $"[QL-Hover] structured queued-status line={line} char={character} " +
                    $"status={queued.Status} avgMs={queued.AverageTranslationMs:0.##}");

                return new QueryLensStructuredHoverResult(
                    Success: false,
                    ErrorMessage: statusMessage,
                    Statements: [],
                    CommandCount: 0,
                    SourceExpression: expression,
                    DbContextType: null,
                    ProviderName: null,
                    SourceFile: filePath,
                    SourceLine: line + 1,
                    Warnings: [],
                    EnrichedSql: null,
                    Mode: "queued",
                    Status: queued.Status,
                    StatusMessage: statusMessage,
                    AvgTranslationMs: queued.AverageTranslationMs);
            }

            var translation = queued.Result;
            if (translation is null)
            {
                sw.Stop();
                Console.Error.WriteLine($"[QL-Hover] structured translate-missing-result line={line} char={character}");
                return Fail("Queued translation completed without a result payload.");
            }

            sw.Stop();
            Console.Error.WriteLine($"[QL-Hover] structured translate-finished line={line} char={character} success={translation.Success} elapsedMs={sw.ElapsedMilliseconds} commands={translation.Commands.Count}");

            if (!translation.Success)
            {
                Console.Error.WriteLine($"[QL-Hover] structured translate-error line={line} char={character} message={translation.ErrorMessage}");
                return Fail(translation.ErrorMessage ?? "Translation failed.");
            }

            var commands = translation.Commands.Count > 0
                ? translation.Commands
                : translation.Sql is null
                    ? []
                    : [new QuerySqlCommand { Sql = translation.Sql, Parameters = translation.Parameters }];

            if (commands.Count == 0)
            {
                Console.Error.WriteLine($"[QL-Hover] structured translate-empty-commands line={line} char={character}");
                return Fail("No SQL was produced for this expression.");
            }

            var providerName = translation.Metadata?.ProviderName;
            var statements = commands.Select((command, index) =>
            {
                var formatted = FormatSqlForDisplay(command.Sql.Trim(), providerName);
                var splitLabel = commands.Count > 1 ? $"Split Query {index + 1} of {commands.Count}" : null;
                return new QueryLensSqlStatement(formatted, splitLabel);
            }).ToList();

            var warnings = translation.Warnings.Select(w => string.IsNullOrWhiteSpace(w.Suggestion)
                ? $"{w.Code}: {w.Message}"
                : $"{w.Code}: {w.Message} ({w.Suggestion})").ToArray();

            var firstSql = statements.Count > 0 ? statements[0].Sql : null;
            var enrichedSql = BuildStructuredEnrichedSql(
                firstSql,
                sourceFile: filePath,
                sourceLine: line + 1,
                sourceExpression: expression,
                dbContextType: translation.Metadata?.DbContextType,
                providerName: translation.Metadata?.ProviderName);

            Console.Error.WriteLine($"[QL-Hover] structured hover-ready line={line} char={character} commands={commands.Count}");
            return new QueryLensStructuredHoverResult(
                Success: true,
                ErrorMessage: null,
                Statements: statements,
                CommandCount: commands.Count,
                SourceExpression: expression,
                DbContextType: translation.Metadata?.DbContextType,
                ProviderName: translation.Metadata?.ProviderName,
                SourceFile: filePath,
                SourceLine: line + 1,
                Warnings: warnings,
                EnrichedSql: enrichedSql,
                Mode: "direct",
                Status: QueryTranslationStatus.Ready,
                StatusMessage: null,
                AvgTranslationMs: queued.AverageTranslationMs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[QL-Hover] structured translate-exception line={line} char={character} type={ex.GetType().Name} message={ex.Message}");
            return Fail($"{ex.GetType().Name}: {ex.Message}", QueryTranslationStatus.Unreachable);
        }
    }

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
