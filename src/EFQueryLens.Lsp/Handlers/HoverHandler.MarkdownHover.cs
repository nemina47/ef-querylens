using System.Diagnostics;
using EFQueryLens.Core;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    public async Task<Hover?> HandleAsync(TextDocumentPositionParams request, CancellationToken cancellationToken)
    {
        var filePath = DocumentPathResolver.Resolve(request.TextDocument.Uri);
        var documentUri = request.TextDocument.Uri.ToString();
        var sourceText = await GetSourceTextAsync(documentUri, filePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return null;
        }

        LogHoverDebug($"hover-request path={filePath} line={request.Position.Line} char={request.Position.Character}");
        var hasSemanticContext = TryResolveSemanticHoverContext(
            filePath,
            sourceText,
            request.Position.Line,
            request.Position.Character,
            out var semanticContext);

        var effectiveLine = hasSemanticContext ? semanticContext!.EffectiveLine : request.Position.Line;
        var effectiveCharacter = hasSemanticContext ? semanticContext!.EffectiveCharacter : request.Position.Character;

        if (effectiveLine != request.Position.Line || effectiveCharacter != request.Position.Character)
        {
            LogHoverDebug(
                $"hover-normalized from line={request.Position.Line} char={request.Position.Character} " +
                $"to line={effectiveLine} char={effectiveCharacter}");
        }

        var cacheKey = BuildHoverCacheKey(
            filePath,
            sourceText,
            request.Position.Line,
            request.Position.Character,
            semanticContext);
        if (TryGetCachedHover(cacheKey, out var cachedHover))
        {
            LogHoverDebug($"hover-cache-hit line={effectiveLine} char={effectiveCharacter}");
            return cachedHover;
        }

        if (semanticContext is not null && TryGetSemanticCachedHover(semanticContext.SemanticKey, out var semanticCachedHover))
        {
            LogHoverDebug($"hover-semantic-cache-hit line={effectiveLine} char={effectiveCharacter}");
            CacheHover(cacheKey, semanticCachedHover, semanticContext);
            return semanticCachedHover;
        }

        if (semanticContext is not null)
        {
            var inFlightKey = BuildInFlightKey(filePath, semanticContext);
            var lazyTask = _inflightSemanticHover.GetOrAdd(
                inFlightKey,
                _ => new Lazy<Task<ComputedHover>>(
                    () => ComputeAndCacheSemanticHoverAsync(
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
                if (sharedResult.Status is QueryTranslationStatus.Ready)
                {
                    CacheHover(cacheKey, sharedResult.Hover, semanticContext);
                }
                return sharedResult.Hover;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var sharedTask = lazyTask.Value;
                var (completed, salvagedResult) = await TryGetResultWithinGraceAsync(sharedTask, _hoverCancellationGraceMs);
                if (completed)
                {
                    if (salvagedResult.Status is QueryTranslationStatus.Ready)
                    {
                        CacheHover(cacheKey, salvagedResult.Hover, semanticContext);
                    }
                    LogHoverDebug(
                        $"hover-cancel-salvaged line={effectiveLine} char={effectiveCharacter} " +
                        $"graceMs={_hoverCancellationGraceMs}");
                    return salvagedResult.Hover;
                }

                if (TryGetSemanticCachedHover(semanticContext.SemanticKey, out var semanticCachedHoverAfterCancel))
                {
                    CacheHover(cacheKey, semanticCachedHoverAfterCancel, semanticContext);
                    LogHoverDebug($"hover-cancel-cache-hit line={effectiveLine} char={effectiveCharacter}");
                    return semanticCachedHoverAfterCancel;
                }

                LogHoverDebug($"hover-canceled line={effectiveLine} char={effectiveCharacter} reason=request-cancelled");
                return null;
            }
        }

        var sw = Stopwatch.StartNew();
        ComputedHover computed;
        try
        {
            computed = await ComputeHoverAsync(
                filePath,
                sourceText,
                effectiveLine,
                effectiveCharacter,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogHoverDebug($"hover-canceled line={effectiveLine} char={effectiveCharacter}");

            // Rider may cancel fast hover requests before translation completes.
            // Warm cache asynchronously so the next hover at the same semantic query can return immediately.
            if (semanticContext is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var warmed = await ComputeHoverAsync(
                            filePath,
                            sourceText,
                            semanticContext.EffectiveLine,
                            semanticContext.EffectiveCharacter,
                            CancellationToken.None);
                        if (warmed.Status is QueryTranslationStatus.Ready)
                        {
                            CacheHover(cacheKey, warmed.Hover, semanticContext);
                            LogHoverDebug($"hover-warm-cache-ready line={semanticContext.EffectiveLine} char={semanticContext.EffectiveCharacter}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogHoverDebug($"hover-warm-cache-failed type={ex.GetType().Name} message={ex.Message}");
                    }
                });
            }

            return null;
        }
        sw.Stop();

        LogHoverDebug($"hover-compute-finished line={effectiveLine} char={effectiveCharacter} elapsedMs={sw.ElapsedMilliseconds} hasResult={computed.Hover is not null}");

        if (computed.Status is QueryTranslationStatus.Ready)
        {
            CacheHover(cacheKey, computed.Hover, semanticContext);
        }
        return computed.Hover;
    }

    private async Task<ComputedHover> ComputeAndCacheSemanticHoverAsync(
        string inFlightKey,
        string cacheKey,
        string filePath,
        string sourceText,
        SemanticHoverContext semanticContext)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var computed = await ComputeHoverAsync(
                filePath,
                sourceText,
                semanticContext.EffectiveLine,
                semanticContext.EffectiveCharacter,
                CancellationToken.None);

            sw.Stop();

            LogHoverDebug(
                $"hover-compute-finished line={semanticContext.EffectiveLine} char={semanticContext.EffectiveCharacter} " +
                $"elapsedMs={sw.ElapsedMilliseconds} hasResult={computed.Hover is not null}");

            if (computed.Status is QueryTranslationStatus.Ready)
            {
                CacheHover(cacheKey, computed.Hover, semanticContext);
            }
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

    private async Task<ComputedHover> ComputeHoverAsync(
        string filePath,
        string sourceText,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var result = await _hoverPreviewService.BuildMarkdownAsync(
            filePath,
            sourceText,
            line,
            character,
            cancellationToken);

        if (result.Status is QueryTranslationStatus.InQueue or QueryTranslationStatus.Starting
            && result.AvgTranslationMs > 0
            && result.AvgTranslationMs < 200
            && _hoverQueuedAdaptiveWaitMs > 0)
        {
            LogHoverDebug(
                $"hover-adaptive-wait line={line} char={character} " +
                $"waitMs={_hoverQueuedAdaptiveWaitMs} avgMs={result.AvgTranslationMs:0.##}");

            await Task.Delay(_hoverQueuedAdaptiveWaitMs, cancellationToken);

            result = await _hoverPreviewService.BuildMarkdownAsync(
                filePath,
                sourceText,
                line,
                character,
                cancellationToken);
        }

        if (!result.Success &&
            result.Output.StartsWith("Could not extract a LINQ query expression", StringComparison.OrdinalIgnoreCase))
        {
            return new ComputedHover(null, result.Status);
        }

        var markdown = result.Success
            ? result.Output
            : $"**QueryLens Error**\n```text\n{result.Output}\n```";

        var content = new MarkupContent
        {
            Kind = MarkupKind.Markdown,
            Value = markdown,
        };

        var hover = new Hover
        {
            Contents = new SumType<SumType<string, MarkedString>, SumType<string, MarkedString>[], MarkupContent>(content),
        };

        return new ComputedHover(hover, result.Status);
    }
}
