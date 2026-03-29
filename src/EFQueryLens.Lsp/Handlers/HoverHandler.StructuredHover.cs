using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    public async Task<QueryLensStructuredHoverResult?> HandleStructuredAsync(TextDocumentPositionParams request, CancellationToken cancellationToken)
    {
        var context = await TryCreateRequestContextAsync(
            request,
            cancellationToken,
            requestLogPrefix: "structured-hover-request",
            logNormalization: false);
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
            LogHoverDebug($"structured-hover-cache-hit status={cachedEntry!.Status} line={effectiveLine} char={effectiveCharacter}");
            return cachedEntry.Structured;
        }

        if (semanticContext is not null
            && TryGetSemanticCachedEntry(semanticContext.SemanticKey, out var semEntry))
        {
            LogHoverDebug($"structured-hover-semantic-cache-hit line={effectiveLine} char={effectiveCharacter}");
            _hoverCache.TryAdd(cacheKey, semEntry!);
            return semEntry!.Structured;
        }

        // Cache-disabled mode: compute immediately so structured hover can still
        // return SQL/status when hoverCacheTtlMs is configured to 0.
        if (_hoverCacheTtlMs <= 0)
        {
            var computed = await ComputeCombinedAsync(
                filePath,
                sourceText,
                effectiveLine,
                effectiveCharacter,
                cancellationToken);

            return computed.Structured;
        }

        // Cache miss — return InQueue immediately and compute in background.
        // BackgroundComputeAndCacheAsync (defined in MarkdownHover.cs) handles both
        // hover and structured formats in a single engine call.
        var inQueueEntry = new ComputedEntry(BuildInQueueHover(), BuildInQueueStructured(), QueryTranslationStatus.InQueue);
        if (TryCacheEntryInQueue(cacheKey, inQueueEntry))
        {
            LogHoverDebug($"structured-hover-cache-miss-queued line={effectiveLine} char={effectiveCharacter}");
            _ = Task.Run(() => BackgroundComputeAndCacheAsync(
                cacheKey, filePath, sourceText, effectiveLine, effectiveCharacter, semanticContext));
        }
        else
        {
            LogHoverDebug($"structured-hover-cache-miss-joined line={effectiveLine} char={effectiveCharacter}");
        }

        return inQueueEntry.Structured;
    }
}
