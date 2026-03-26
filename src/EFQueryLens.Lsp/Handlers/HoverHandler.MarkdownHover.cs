using System.Diagnostics;
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

        if (TryGetCachedHover(cacheKey, out var cachedHover))
        {
            LogHoverDebug($"hover-cache-hit line={effectiveLine} char={effectiveCharacter}");
            return cachedHover;
        }

        if (semanticContext is not null
            && _hoverCacheTtlMs > 0
            && _semanticHoverCache.TryGetValue(semanticContext.SemanticKey, out var semEntry)
            && !IsExpired(semEntry)
            && semEntry.Hover is not null)
        {
            LogHoverDebug($"hover-semantic-cache-hit line={effectiveLine} char={effectiveCharacter}");
            _hoverCache.TryAdd(cacheKey, semEntry);
            return semEntry.Hover;
        }

        if (semanticContext is not null)
        {
            var inFlightKey = BuildInFlightKey(filePath, semanticContext);
            var lazyTask = _inflightSemanticHover.GetOrAdd(
                inFlightKey,
                _ => new Lazy<Task<ComputedEntry>>(
                    () => ComputeAndCacheCombinedAsync(
                        inFlightKey,
                        cacheKey,
                        filePath,
                        sourceText,
                        semanticContext),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            if (lazyTask.IsValueCreated)
            {
                LogHoverDebug($"hover-inflight-join line={effectiveLine} char={effectiveCharacter}");
            }
            else
            {
                LogHoverDebug($"hover-inflight-start line={effectiveLine} char={effectiveCharacter}");
            }

            try
            {
                var sharedTask = lazyTask.Value;
                var sharedResult = await WaitWithCancellationAsync(sharedTask, cancellationToken);
                CacheEntry(cacheKey, sharedResult, semanticContext);
                return sharedResult.Hover;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var sharedTask = lazyTask.Value;
                var (completed, salvagedResult) = await TryGetResultWithinGraceAsync(sharedTask, _hoverCancellationGraceMs);
                if (completed)
                {
                    CacheEntry(cacheKey, salvagedResult, semanticContext);
                    LogHoverDebug(
                        $"hover-cancel-salvaged line={effectiveLine} char={effectiveCharacter} " +
                        $"graceMs={_hoverCancellationGraceMs}");
                    return salvagedResult.Hover;
                }

                if (_hoverCacheTtlMs > 0
                    && _semanticHoverCache.TryGetValue(semanticContext.SemanticKey, out var semEntryAfterCancel)
                    && !IsExpired(semEntryAfterCancel))
                {
                    _hoverCache.TryAdd(cacheKey, semEntryAfterCancel);
                    LogHoverDebug($"hover-cancel-cache-hit line={effectiveLine} char={effectiveCharacter}");
                    return semEntryAfterCancel.Hover;
                }

                var fallbackHover = await TryBuildCanceledStatusFallbackHoverAsync(
                    filePath,
                    sourceText,
                    effectiveLine,
                    effectiveCharacter);
                if (fallbackHover is not null)
                {
                    LogHoverDebug($"hover-cancel-fallback-hit line={effectiveLine} char={effectiveCharacter}");
                    return fallbackHover;
                }

                LogHoverDebug($"hover-canceled line={effectiveLine} char={effectiveCharacter} reason=request-cancelled");
                return null;
            }
        }

        var sw = Stopwatch.StartNew();
        ComputedEntry computed;
        try
        {
            computed = await ComputeCombinedAsync(
                filePath,
                sourceText,
                effectiveLine,
                effectiveCharacter,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogHoverDebug($"hover-canceled line={effectiveLine} char={effectiveCharacter}");

            var fallbackHover = await TryBuildCanceledStatusFallbackHoverAsync(
                filePath,
                sourceText,
                effectiveLine,
                effectiveCharacter);
            if (fallbackHover is not null)
            {
                LogHoverDebug($"hover-cancel-fallback-hit line={effectiveLine} char={effectiveCharacter}");
                return fallbackHover;
            }

            return null;
        }
        sw.Stop();

        LogHoverDebug($"hover-compute-finished line={effectiveLine} char={effectiveCharacter} elapsedMs={sw.ElapsedMilliseconds} hasResult={computed.Hover is not null}");
        CacheEntry(cacheKey, computed, semanticContext);
        return computed.Hover;
    }

    // Shared inflight delegate: computes both hover and structured in a single engine call.
    // Both HandleAsync and HandleStructuredAsync use _inflightSemanticHover to join this task.
    private async Task<ComputedEntry> ComputeAndCacheCombinedAsync(
        string inFlightKey,
        string cacheKey,
        string filePath,
        string sourceText,
        SemanticHoverContext semanticContext)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var computed = await ComputeCombinedAsync(
                filePath,
                sourceText,
                semanticContext.EffectiveLine,
                semanticContext.EffectiveCharacter,
                CancellationToken.None);

            sw.Stop();
            LogHoverDebug(
                $"hover-compute-finished line={semanticContext.EffectiveLine} char={semanticContext.EffectiveCharacter} " +
                $"elapsedMs={sw.ElapsedMilliseconds} hasResult={computed.Hover is not null}");

            CacheEntry(cacheKey, computed, semanticContext);
            return computed;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogHoverDebug(
                $"hover-compute-failed line={semanticContext.EffectiveLine} char={semanticContext.EffectiveCharacter} " +
                $"elapsedMs={sw.ElapsedMilliseconds} type={ex.GetType().Name} message={ex.Message}");
            throw;
        }
        finally
        {
            _inflightSemanticHover.TryRemove(inFlightKey, out _);
        }
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

    private async Task<Hover?> TryBuildCanceledStatusFallbackHoverAsync(
        string filePath,
        string sourceText,
        int line,
        int character)
    {
        using var fallbackCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            var fallback = await _hoverPreviewService.BuildMarkdownAsync(
                filePath,
                sourceText,
                line,
                character,
                fallbackCts.Token);

            if (!fallback.Success)
            {
                return null;
            }

            if (fallback.Status is QueryTranslationStatus.Starting
                or QueryTranslationStatus.InQueue
                or QueryTranslationStatus.DaemonUnavailable)
            {
                return CreateMarkdownHover(fallback.Output);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore fallback timeout and keep the primary cancellation behavior.
        }
        catch (Exception ex)
        {
            LogHoverDebug($"hover-cancel-fallback-failed line={line} char={character} type={ex.GetType().Name} message={ex.Message}");
        }

        return null;
    }

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
