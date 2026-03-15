using EFQueryLens.Core;
using EFQueryLens.Lsp.Parsing;
using System.Diagnostics;

namespace EFQueryLens.Lsp.Services;

internal sealed partial class HoverPreviewService
{
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
}
