using EFQueryLens.Core;
using EFQueryLens.Lsp.Parsing;
using System.Diagnostics;

namespace EFQueryLens.Lsp.Services;

internal sealed partial class HoverPreviewService
{
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
}
