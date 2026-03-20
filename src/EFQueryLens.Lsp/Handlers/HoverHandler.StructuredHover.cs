using System.Diagnostics;
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

        if (TryGetCachedStructured(cacheKey, out var cachedResult))
        {
            LogHoverDebug($"structured-hover-cache-hit line={effectiveLine} char={effectiveCharacter}");
            return cachedResult;
        }

        if (semanticContext is not null && TryGetSemanticCachedStructured(semanticContext.SemanticKey, out var semanticCached))
        {
            LogHoverDebug($"structured-hover-semantic-cache-hit line={effectiveLine} char={effectiveCharacter}");
            CacheStructured(cacheKey, semanticCached, semanticContext);
            return semanticCached;
        }

        if (semanticContext is not null)
        {
            var inFlightKey = BuildInFlightKey(filePath, semanticContext);
            var lazyTask = _inflightSemanticStructuredHover.GetOrAdd(
                inFlightKey,
                _ => new Lazy<Task<QueryLensStructuredHoverResult?>>(
                    () => ComputeAndCacheStructuredSemanticHoverAsync(
                        inFlightKey,
                        cacheKey,
                        filePath,
                        sourceText,
                        semanticContext),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                var sharedTask = lazyTask.Value;
                var sharedResult = await WaitWithCancellationAsync(sharedTask, cancellationToken);
                CacheStructured(cacheKey, sharedResult, semanticContext);
                return sharedResult;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var sharedTask = lazyTask.Value;
                var (completed, salvagedResult) = await TryGetResultWithinGraceAsync(sharedTask, _hoverCancellationGraceMs);
                if (completed)
                {
                    CacheStructured(cacheKey, salvagedResult, semanticContext);
                    LogHoverDebug($"structured-hover-cancel-salvaged line={effectiveLine} char={effectiveCharacter}");
                    return salvagedResult;
                }

                if (TryGetSemanticCachedStructured(semanticContext.SemanticKey, out var semanticCachedAfterCancel))
                {
                    CacheStructured(cacheKey, semanticCachedAfterCancel, semanticContext);
                    return semanticCachedAfterCancel;
                }

                return null;
            }
        }

        var sw = Stopwatch.StartNew();
        QueryLensStructuredHoverResult? computed;
        try
        {
            computed = await ComputeStructuredHoverAsync(
                filePath,
                sourceText,
                effectiveLine,
                effectiveCharacter,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogHoverDebug($"structured-hover-canceled line={effectiveLine} char={effectiveCharacter}");
            return null;
        }

        sw.Stop();
        LogHoverDebug($"structured-hover-compute-finished line={effectiveLine} char={effectiveCharacter} elapsedMs={sw.ElapsedMilliseconds} hasResult={computed is not null}");
        CacheStructured(cacheKey, computed, semanticContext);
        return computed;
    }

    private async Task<QueryLensStructuredHoverResult?> ComputeAndCacheStructuredSemanticHoverAsync(
        string inFlightKey,
        string cacheKey,
        string filePath,
        string sourceText,
        SemanticHoverContext semanticContext)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var computed = await ComputeStructuredHoverAsync(
                filePath,
                sourceText,
                semanticContext.EffectiveLine,
                semanticContext.EffectiveCharacter,
                CancellationToken.None);
            sw.Stop();
            LogHoverDebug(
                $"structured-hover-compute-finished line={semanticContext.EffectiveLine} char={semanticContext.EffectiveCharacter} " +
                $"elapsedMs={sw.ElapsedMilliseconds} hasResult={computed is not null}");
            CacheStructured(cacheKey, computed, semanticContext);
            return computed;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogHoverDebug(
                $"structured-hover-compute-failed line={semanticContext.EffectiveLine} char={semanticContext.EffectiveCharacter} " +
                $"elapsedMs={sw.ElapsedMilliseconds} type={ex.GetType().Name} message={ex.Message}");
            throw;
        }
        finally
        {
            _inflightSemanticStructuredHover.TryRemove(inFlightKey, out _);
        }
    }

    private async Task<QueryLensStructuredHoverResult?> ComputeStructuredHoverAsync(
        string filePath,
        string sourceText,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var result = await _hoverPreviewService.BuildStructuredAsync(
            filePath,
            sourceText,
            line,
            character,
            cancellationToken);

        if (result.Status is QueryTranslationStatus.InQueue or QueryTranslationStatus.Starting
            && result.AvgTranslationMs > 0
            && result.AvgTranslationMs < _structuredQueuedAdaptiveWaitMs
            && _structuredQueuedAdaptiveWaitMs > 0)
        {
            LogHoverDebug(
                $"structured-hover-adaptive-wait line={line} char={character} " +
                $"waitMs={_structuredQueuedAdaptiveWaitMs} avgMs={result.AvgTranslationMs:0.##}");

            await Task.Delay(_structuredQueuedAdaptiveWaitMs, cancellationToken);

            var secondAttempt = await _hoverPreviewService.BuildStructuredAsync(
                filePath,
                sourceText,
                line,
                character,
                cancellationToken);

            if (secondAttempt.Status is QueryTranslationStatus.Ready)
            {
                return secondAttempt;
            }

            return result;
        }

        if (!result.Success &&
            result.ErrorMessage?.StartsWith("Could not extract a LINQ query expression", StringComparison.OrdinalIgnoreCase) == true)
        {
            return null;
        }

        return result;
    }
}
