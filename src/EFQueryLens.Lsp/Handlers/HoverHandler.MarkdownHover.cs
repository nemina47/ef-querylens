using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    public async Task<Hover?> HandleAsync(TextDocumentPositionParams request, CancellationToken cancellationToken)
    {
        var context = await TryCreateRequestContextAsync(
            request,
            cancellationToken,
            requestLogPrefix: "hover-request",
            logNormalization: true);
        if (context is null)
        {
            return null;
        }

        var filePath = context.FilePath;
        var sourceText = context.SourceText;
        var semanticContext = context.SemanticContext;
        var effectiveLine = context.EffectiveLine;
        var effectiveCharacter = context.EffectiveCharacter;
        var cacheKey = context.CacheKey;

        if (TryGetCachedEntry(cacheKey, out var cachedEntry))
        {
            LogHoverDebug($"hover-cache-hit status={cachedEntry!.Status} line={effectiveLine} char={effectiveCharacter}");
            return cachedEntry.Hover;
        }

        if (semanticContext is not null
            && TryGetSemanticCachedEntry(semanticContext.SemanticKey, out var semEntry))
        {
            LogHoverDebug($"hover-semantic-cache-hit line={effectiveLine} char={effectiveCharacter}");
            _hoverCache.TryAdd(cacheKey, semEntry!);
            return semEntry!.Hover;
        }

        // Cache-disabled mode: compute immediately so users do not get a perpetual
        // "computing" placeholder when hoverCacheTtlMs is configured to 0.
        if (_hoverCacheTtlMs <= 0)
        {
            var computed = await ComputeCombinedAsync(
                filePath,
                sourceText,
                effectiveLine,
                effectiveCharacter,
                cancellationToken);

            return computed.Hover;
        }

        // Cache miss — return InQueue immediately and compute in background.
        // TryCacheEntryInQueue uses TryAdd so only one concurrent hover wins the race
        // and starts the background task; others read the InQueue entry next hover.
        var inQueueEntry = new ComputedEntry(BuildInQueueHover(), BuildInQueueStructured(), QueryTranslationStatus.InQueue);
        if (TryCacheEntryInQueue(cacheKey, inQueueEntry))
        {
            LogHoverDebug($"hover-cache-miss-queued line={effectiveLine} char={effectiveCharacter}");
            _ = Task.Run(() => BackgroundComputeAndCacheAsync(
                cacheKey, filePath, sourceText, effectiveLine, effectiveCharacter, semanticContext));
        }
        else
        {
            LogHoverDebug($"hover-cache-miss-joined line={effectiveLine} char={effectiveCharacter}");
        }

        return inQueueEntry.Hover;
    }

    private async Task<ComputedEntry> ComputeCombinedAsync(
        string filePath,
        string sourceText,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var combined = await _hoverPreviewService.BuildCombinedAsync(
            filePath,
            sourceText,
            line,
            character,
            cancellationToken);

        var adaptiveWaitMs = Math.Max(_hoverQueuedAdaptiveWaitMs, _structuredQueuedAdaptiveWaitMs);
        if (combined.Markdown.Status is QueryTranslationStatus.InQueue or QueryTranslationStatus.Starting
            && combined.Markdown.AvgTranslationMs > 0
            && combined.Markdown.AvgTranslationMs < adaptiveWaitMs
            && adaptiveWaitMs > 0)
        {
            LogHoverDebug(
                $"hover-adaptive-wait line={line} char={character} " +
                $"waitMs={adaptiveWaitMs} avgMs={combined.Markdown.AvgTranslationMs:0.##}");

            await Task.Delay(adaptiveWaitMs, cancellationToken);

            combined = await _hoverPreviewService.BuildCombinedAsync(
                filePath,
                sourceText,
                line,
                character,
                cancellationToken);
        }

        return BuildComputedFromCombined(combined);
    }

    /// <summary>
    /// Converts a <see cref="CombinedHoverResult"/> into a <see cref="ComputedEntry"/> by
    /// applying the "Could not extract" normalisation rules.  Shared by
    /// <see cref="ComputeCombinedAsync"/> and <see cref="StorePrewarmedEntry"/>.
    /// </summary>
    private static ComputedEntry BuildComputedFromCombined(CombinedHoverResult combined)
    {
        Hover? hover = null;
        if (combined.Markdown.Success
            || !combined.Markdown.Output.StartsWith("Could not extract a LINQ query expression", StringComparison.OrdinalIgnoreCase))
        {
            var markdownText = combined.Markdown.Success
                ? combined.Markdown.Output
                : $"**QueryLens Error**\n```text\n{combined.Markdown.Output}\n```";
            hover = CreateMarkdownHover(markdownText);
        }

        // Normalize "Could not extract" structured result to null — no hover at this position.
        QueryLensStructuredHoverResult? structured = combined.Structured;
        if (structured is { Success: false }
            && structured.ErrorMessage?.StartsWith("Could not extract a LINQ query expression", StringComparison.OrdinalIgnoreCase) == true)
        {
            structured = null;
        }

        return new ComputedEntry(hover, structured, combined.Markdown.Status);
    }

    /// <summary>
    /// Runs <see cref="ComputeCombinedAsync"/> on a background thread and writes the result
    /// into the hover cache, replacing the <see cref="QueryTranslationStatus.InQueue"/>
    /// placeholder that was stored when the hover request first missed the cache.
    ///
    /// On failure the placeholder is evicted so the next hover retries computation cleanly.
    /// </summary>
    private async Task BackgroundComputeAndCacheAsync(
        string cacheKey,
        string filePath,
        string sourceText,
        int line,
        int character,
        SemanticHoverContext? semanticContext)
    {
        try
        {
            var computed = await ComputeCombinedAsync(filePath, sourceText, line, character, CancellationToken.None);
            CacheEntry(cacheKey, computed, semanticContext);
            LogHoverDebug($"bg-compute-finished key={cacheKey} status={computed.Status}");
        }
        catch (Exception ex)
        {
            // Evict the InQueue placeholder so the next hover re-triggers computation
            // rather than showing "computing..." indefinitely.
            _hoverCache.TryRemove(cacheKey, out _);
            LogHoverDebug($"bg-compute-failed key={cacheKey} type={ex.GetType().Name} message={ex.Message}");
        }
    }

    private static Hover BuildInQueueHover() =>
        CreateMarkdownHover("**EF QueryLens** \u2014 computing SQL\u2026 hover again in a moment.");

    internal static QueryLensStructuredHoverResult BuildInQueueStructured() =>
        new(
            Success: false,
            ErrorMessage: null,
            Statements: [],
            CommandCount: 0,
            SourceExpression: null,
            ExecutedExpression: null,
            DbContextType: null,
            ProviderName: null,
            SourceFile: null,
            SourceLine: 0,
            Warnings: [],
            EnrichedSql: null,
            Mode: null,
            Status: QueryTranslationStatus.InQueue,
            StatusMessage: "Computing SQL \u2014 hover again in a moment.",
            AvgTranslationMs: 0);

    private static Hover CreateMarkdownHover(string markdown)
    {
        var content = new MarkupContent
        {
            Kind = MarkupKind.Markdown,
            Value = markdown,
        };

        return new Hover
        {
            Contents = new SumType<SumType<string, MarkedString>, SumType<string, MarkedString>[], MarkupContent>(content),
        };
    }
}
