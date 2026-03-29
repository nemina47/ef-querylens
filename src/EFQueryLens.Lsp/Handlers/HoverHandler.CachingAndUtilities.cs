using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    private const int CacheMaxEntries = 2_000;
    private const int CacheTargetEntries = 1_600;

    /// <summary>
    /// Looks up the primary position/fingerprint cache.
    /// Returns <c>true</c> for both <see cref="QueryTranslationStatus.Ready"/> and
    /// <see cref="QueryTranslationStatus.InQueue"/> entries so callers can render the
    /// appropriate status message immediately without re-triggering computation.
    /// </summary>
    private bool TryGetCachedEntry(string cacheKey, out CachedEntry? entry)
    {
        entry = null;
        if (_hoverCacheTtlMs <= 0) return false;
        if (!_hoverCache.TryGetValue(cacheKey, out var cached)) return false;
        if (IsExpired(cached)) { _hoverCache.TryRemove(cacheKey, out _); return false; }
        entry = cached;
        return true;
    }

    /// <summary>
    /// Looks up the semantic (expression-normalised) cache. Only returns
    /// <see cref="QueryTranslationStatus.Ready"/> entries — <c>InQueue</c> placeholders
    /// are intentionally excluded so they don't suppress real results at other cursor
    /// positions within the same chain.
    /// </summary>
    private bool TryGetSemanticCachedEntry(string semanticKey, out CachedEntry? entry)
    {
        entry = null;
        if (_hoverCacheTtlMs <= 0) return false;
        if (!_semanticHoverCache.TryGetValue(semanticKey, out var cached)) return false;
        if (IsExpired(cached)) { _semanticHoverCache.TryRemove(semanticKey, out _); return false; }
        if (cached.Status is not QueryTranslationStatus.Ready) return false;
        entry = cached;
        return true;
    }

    private bool IsExpired(CachedEntry entry)
    {
        // InQueue placeholders use a shorter TTL so a stale "computing..." message
        // doesn't linger if the background task silently failed or was never started.
        var ttlMs = entry.Status is QueryTranslationStatus.InQueue
            ? _inQueueCacheTtlMs
            : _hoverCacheTtlMs;

        return entry.CreatedAtTicks + TimeSpan.FromMilliseconds(ttlMs).Ticks <= DateTime.UtcNow.Ticks;
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

    /// <summary>
    /// Atomically writes an <see cref="QueryTranslationStatus.InQueue"/> placeholder to the
    /// primary cache using <c>TryAdd</c>. Returns <c>true</c> if this call won the race
    /// and is responsible for starting the background compute task; <c>false</c> if another
    /// concurrent hover already wrote the placeholder (no second task should be started).
    /// </summary>
    private bool TryCacheEntryInQueue(string cacheKey, ComputedEntry entry)
    {
        if (_hoverCacheTtlMs <= 0) return false;
        var cached = new CachedEntry(DateTime.UtcNow.Ticks, entry.Hover, entry.Structured, QueryTranslationStatus.InQueue);
        return _hoverCache.TryAdd(cacheKey, cached);
    }

    private void CacheEntry(string cacheKey, ComputedEntry entry, SemanticHoverContext? semanticContext)
    {
        if (_hoverCacheTtlMs <= 0) return;

        // Store statuses that should replace an InQueue placeholder in the primary cache.
        // Keep the semantic cache Ready-only (below) so cross-position dedupe only uses
        // finalized results.
        if (entry.Status is not (QueryTranslationStatus.Ready
            or QueryTranslationStatus.InQueue
            or QueryTranslationStatus.Starting
            or QueryTranslationStatus.DaemonUnavailable))
        {
            return;
        }

        var cached = new CachedEntry(DateTime.UtcNow.Ticks, entry.Hover, entry.Structured, entry.Status);
        _hoverCache[cacheKey] = cached;

        // InQueue placeholders go only to the primary cache — never the semantic cache.
        // The semantic cache is exclusively for cross-position deduplication of real results,
        // and an InQueue entry there would suppress valid results at adjacent cursor positions.
        if (entry.Status is QueryTranslationStatus.Ready
            && semanticContext is not null
            && (entry.Hover is not null || entry.Structured is not null))
        {
            _semanticHoverCache[semanticContext.SemanticKey] = cached;
        }

        TrimCacheIfNeeded(_hoverCache, static e => e.CreatedAtTicks);
        TrimCacheIfNeeded(_semanticHoverCache, static e => e.CreatedAtTicks);
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

    private sealed record CachedEntry(
        long CreatedAtTicks,
        Hover? Hover,
        QueryLensStructuredHoverResult? Structured,
        QueryTranslationStatus Status = QueryTranslationStatus.Ready);

    private readonly record struct ComputedEntry(Hover? Hover, QueryLensStructuredHoverResult? Structured, QueryTranslationStatus Status);
}
