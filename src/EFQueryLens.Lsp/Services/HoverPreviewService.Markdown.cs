using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;

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
        Action<string> log = message => Console.Error.WriteLine($"[QL-Hover] {message}");
        var canonical = await BuildCanonicalAsync(
            filePath,
            sourceText,
            line,
            character,
            cancellationToken,
            log);

        var result = FormatMarkdown(canonical, filePath, line, character);
        if (result.Success && result.Status is QueryTranslationStatus.Ready)
        {
            Console.Error.WriteLine($"[QL-Hover] hover-markdown-ready line={line} char={character} markdownLen={result.Output.Length}");
        }

        return result;
    }

    private HoverPreviewComputationResult FormatMarkdown(
        HoverCanonicalComputationResult canonical,
        string filePath,
        int line,
        int character)
    {
        if (canonical.Status is not QueryTranslationStatus.Ready && canonical.Success)
        {
            return new HoverPreviewComputationResult(
                Success: true,
                Output: BuildQueuedStatusMarkdown(canonical.Status, canonical.Message, canonical.AvgTranslationMs),
                Status: canonical.Status,
                AvgTranslationMs: canonical.AvgTranslationMs,
                LastTranslationMs: canonical.LastTranslationMs);
        }

        if (!canonical.Success)
        {
            return new HoverPreviewComputationResult(false, canonical.Message, canonical.Status);
        }

        var documentUri = DocumentPathResolver.ToUri(filePath);
        var markdown = BuildHoverMarkdown(
            canonical.Commands,
            canonical.Warnings,
            documentUri,
            line,
            character,
            canonical.Metadata,
            canonical.LastTranslationMs > 0 ? canonical.LastTranslationMs : canonical.AvgTranslationMs,
            _useBrowserSafeHoverActionLinks,
            _actionPort);

        Console.Error.WriteLine(
            $"[QL-Hover] hover-links: actionPort={_actionPort} " +
            $"copyLink={(markdown.Contains("http://127.0.0.1") ? "http-localhost" : "efquerylens-scheme")}");

        return new HoverPreviewComputationResult(
            true,
            markdown,
            QueryTranslationStatus.Ready,
            canonical.AvgTranslationMs,
            canonical.LastTranslationMs);
    }

    private static string BuildQueuedStatusMarkdown(
        QueryTranslationStatus status,
        string statusText,
        double avgTranslationMs)
    {
        _ = status;
        _ = avgTranslationMs;
        var normalizedStatusText = string.IsNullOrWhiteSpace(statusText)
            ? "EF QueryLens - in queue"
            : statusText;
        return $"{normalizedStatusText}\n\n";
    }
}
