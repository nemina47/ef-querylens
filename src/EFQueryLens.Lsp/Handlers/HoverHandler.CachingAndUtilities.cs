using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    private const int CacheMaxEntries = 2_000;
    private const int CacheTargetEntries = 1_600;

    private bool TryGetCachedStructured(string cacheKey, out QueryLensStructuredHoverResult? result)
    {
        result = null;
        if (_hoverCacheTtlMs <= 0) return false;
        if (!_structuredHoverCache.TryGetValue(cacheKey, out var cached)) return false;
        var expiresAtTicks = cached.CreatedAtTicks + TimeSpan.FromMilliseconds(_hoverCacheTtlMs).Ticks;
        if (expiresAtTicks <= DateTime.UtcNow.Ticks)
        {
            _structuredHoverCache.TryRemove(cacheKey, out _);
            return false;
        }
        result = cached.Result;
        return true;
    }

    private bool TryGetSemanticCachedStructured(string semanticKey, out QueryLensStructuredHoverResult? result)
    {
        result = null;
        if (_hoverCacheTtlMs <= 0) return false;
        if (!_semanticStructuredHoverCache.TryGetValue(semanticKey, out var cached)) return false;
        var expiresAtTicks = cached.CreatedAtTicks + TimeSpan.FromMilliseconds(_hoverCacheTtlMs).Ticks;
        if (expiresAtTicks <= DateTime.UtcNow.Ticks)
        {
            _semanticStructuredHoverCache.TryRemove(semanticKey, out _);
            return false;
        }
        result = cached.Result;
        return result is not null;
    }

    private void CacheStructured(string cacheKey, QueryLensStructuredHoverResult? result, SemanticHoverContext? semanticContext)
    {
        if (_hoverCacheTtlMs <= 0) return;
        if (result is null || result.Status is not QueryTranslationStatus.Ready) return;

        _structuredHoverCache[cacheKey] = new CachedStructuredResult(DateTime.UtcNow.Ticks, result);
        if (semanticContext is not null)
        {
            _semanticStructuredHoverCache[semanticContext.SemanticKey] = new CachedStructuredResult(DateTime.UtcNow.Ticks, result);
        }

        TrimCacheIfNeeded(_structuredHoverCache, static cached => cached.CreatedAtTicks);
        TrimCacheIfNeeded(_semanticStructuredHoverCache, static cached => cached.CreatedAtTicks);
    }

    private async Task<string?> GetSourceTextAsync(string documentUri, string filePath, CancellationToken cancellationToken)
    {
        var sourceText = _documentManager.GetDocumentText(documentUri);
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            return sourceText;
        }

        if (!File.Exists(filePath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(filePath, cancellationToken);
    }

    private bool TryGetCachedHover(string cacheKey, out Hover? hover)
    {
        hover = null;

        if (_hoverCacheTtlMs <= 0)
        {
            return false;
        }

        if (!_hoverCache.TryGetValue(cacheKey, out var cached))
        {
            return false;
        }

        var expiresAtTicks = cached.CreatedAtTicks + TimeSpan.FromMilliseconds(_hoverCacheTtlMs).Ticks;
        if (expiresAtTicks <= DateTime.UtcNow.Ticks)
        {
            _hoverCache.TryRemove(cacheKey, out _);
            return false;
        }

        hover = cached.Hover;
        return true;
    }

    private bool TryGetSemanticCachedHover(string semanticKey, out Hover? hover)
    {
        hover = null;

        if (_hoverCacheTtlMs <= 0)
        {
            return false;
        }

        if (!_semanticHoverCache.TryGetValue(semanticKey, out var cached))
        {
            return false;
        }

        var expiresAtTicks = cached.CreatedAtTicks + TimeSpan.FromMilliseconds(_hoverCacheTtlMs).Ticks;
        if (expiresAtTicks <= DateTime.UtcNow.Ticks)
        {
            _semanticHoverCache.TryRemove(semanticKey, out _);
            return false;
        }

        hover = cached.Hover;
        return hover is not null;
    }

    private static async Task<T> WaitWithCancellationAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await task;
        }

        if (task.IsCompleted)
        {
            return await task;
        }

        var cancellationTaskSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellationTaskSource);

        var completed = await Task.WhenAny(task, cancellationTaskSource.Task);
        if (completed != task)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await task;
    }

    private static async Task<(bool Completed, T? Result)> TryGetResultWithinGraceAsync<T>(Task<T> task, int graceMilliseconds)
    {
        if (graceMilliseconds <= 0)
        {
            return (false, default);
        }

        if (task.IsCompleted)
        {
            try
            {
                return (true, await task);
            }
            catch
            {
                return (false, default);
            }
        }

        var completed = await Task.WhenAny(task, Task.Delay(graceMilliseconds));
        if (completed != task)
        {
            return (false, default);
        }

        try
        {
            return (true, await task);
        }
        catch
        {
            return (false, default);
        }
    }

    private void CacheHover(string cacheKey, Hover? hover, SemanticHoverContext? semanticContext)
    {
        if (_hoverCacheTtlMs <= 0)
        {
            return;
        }

        _hoverCache[cacheKey] = new CachedHoverResult(DateTime.UtcNow.Ticks, hover);

        if (semanticContext is not null && hover is not null)
        {
            _semanticHoverCache[semanticContext.SemanticKey] = new CachedHoverResult(DateTime.UtcNow.Ticks, hover);
        }

        TrimCacheIfNeeded(_hoverCache, static cached => cached.CreatedAtTicks);
        TrimCacheIfNeeded(_semanticHoverCache, static cached => cached.CreatedAtTicks);
    }

    private static void TrimCacheIfNeeded<TValue>(
        System.Collections.Concurrent.ConcurrentDictionary<string, TValue> cache,
        Func<TValue, long> createdAtTicksAccessor)
    {
        if (cache.Count <= CacheMaxEntries)
        {
            return;
        }

        var removeCount = cache.Count - CacheTargetEntries;
        if (removeCount <= 0)
        {
            return;
        }

        var keysToRemove = cache
            .OrderBy(pair => createdAtTicksAccessor(pair.Value))
            .Take(removeCount)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in keysToRemove)
        {
            cache.TryRemove(key, out _);
        }
    }

    private void InvalidateCaches(string reason)
    {
        _hoverCache.Clear();
        _semanticHoverCache.Clear();
        _structuredHoverCache.Clear();
        _semanticStructuredHoverCache.Clear();
        _inflightSemanticHover.Clear();
        _inflightSemanticStructuredHover.Clear();

        LogHoverDebug($"hover-cache-invalidated reason={reason}");
    }

    private void LogHoverDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-Hover] {message}");
    }

    private sealed record CachedHoverResult(long CreatedAtTicks, Hover? Hover);

    private sealed record CachedStructuredResult(long CreatedAtTicks, QueryLensStructuredHoverResult? Result);

    private readonly record struct ComputedHover(Hover? Hover, QueryTranslationStatus Status);
}
