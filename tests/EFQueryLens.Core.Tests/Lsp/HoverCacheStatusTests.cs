using System.Reflection;
using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Core.Tests.Lsp;

/// <summary>
/// Verifies Phase 2 of the hover cache redesign:
///   - <c>CachedEntry</c> carries a <c>QueryTranslationStatus</c>.
///   - <c>InQueue</c> entries are stored and retrievable from the primary cache.
///   - <c>InQueue</c> entries respect their own (shorter) TTL.
///   - <c>InQueue</c> entries are never stored in the semantic cache.
///   - Writing a <c>Ready</c> entry over the same key replaces an <c>InQueue</c> entry.
///   - Semantic cache only returns <c>Ready</c> entries.
/// </summary>
public class HoverCacheStatusTests
{
    // ── InQueue stored and retrievable ───────────────────────────────────────

    [Fact]
    public void CacheEntry_InQueue_IsStoredAndRetrievable()
    {
        var handler = CreateHandler();
        var (inQueueComputed, _) = MakeEntries(QueryTranslationStatus.InQueue);

        InvokeCacheEntry(handler, "k1", inQueueComputed, semanticContext: null);

        Assert.True(InvokeTryGetCachedEntry(handler, "k1", out var retrieved));
        Assert.NotNull(retrieved);
        Assert.Equal(QueryTranslationStatus.InQueue, GetEntryStatus(retrieved!));
    }

    [Fact]
    public void CacheEntry_Ready_IsStoredAndRetrievable()
    {
        var handler = CreateHandler();
        var (readyComputed, _) = MakeEntries(QueryTranslationStatus.Ready);

        InvokeCacheEntry(handler, "k2", readyComputed, semanticContext: null);

        Assert.True(InvokeTryGetCachedEntry(handler, "k2", out var retrieved));
        Assert.NotNull(retrieved);
        Assert.Equal(QueryTranslationStatus.Ready, GetEntryStatus(retrieved!));
    }

    // ── Ready replaces InQueue ───────────────────────────────────────────────

    [Fact]
    public void CacheEntry_ReadyOverInQueue_ReplacesEntry()
    {
        var handler = CreateHandler();
        var (inQueueComputed, _) = MakeEntries(QueryTranslationStatus.InQueue);
        var (readyComputed, _) = MakeEntries(QueryTranslationStatus.Ready);

        InvokeCacheEntry(handler, "k3", inQueueComputed, semanticContext: null);
        InvokeCacheEntry(handler, "k3", readyComputed, semanticContext: null);

        Assert.True(InvokeTryGetCachedEntry(handler, "k3", out var retrieved));
        Assert.Equal(QueryTranslationStatus.Ready, GetEntryStatus(retrieved!));
    }

    // ── InQueue never written to semantic cache ───────────────────────────────

    [Fact]
    public void CacheEntry_InQueue_NeverWrittenToSemanticCache()
    {
        var handler = CreateHandler();
        var semCtx = CreateSemanticHoverContext("sem-key-inqueue", 1, 1);
        var (inQueueComputed, _) = MakeEntries(QueryTranslationStatus.InQueue);

        InvokeCacheEntry(handler, "k4", inQueueComputed, semCtx);

        // Semantic cache should remain empty for InQueue entries.
        Assert.Equal(0, GetDictionaryCount(handler, "_semanticHoverCache"));
    }

    [Fact]
    public void CacheEntry_Ready_WrittenToSemanticCache()
    {
        var handler = CreateHandler();
        var semCtx = CreateSemanticHoverContext("sem-key-ready", 1, 1);
        var (readyComputed, _) = MakeEntries(QueryTranslationStatus.Ready);

        InvokeCacheEntry(handler, "k5", readyComputed, semCtx);

        Assert.Equal(1, GetDictionaryCount(handler, "_semanticHoverCache"));
    }

    // ── Semantic cache only returns Ready entries ─────────────────────────────

    [Fact]
    public void TryGetSemanticCachedEntry_DoesNotReturnInQueueEntries()
    {
        // Directly inject an InQueue entry into _semanticHoverCache via reflection
        // and verify that TryGetSemanticCachedEntry refuses to surface it.
        var handler = CreateHandler();
        var inQueueEntry = CreateCachedEntry(
            DateTime.UtcNow.Ticks,
            hover: new Hover(),
            structured: null,
            status: QueryTranslationStatus.InQueue);

        // Use the indexer setter via reflection since CachedEntry is a private type.
        var field = typeof(HoverHandler).GetField("_semanticHoverCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = field.GetValue(handler)!;
        var indexer = dict.GetType().GetProperty("Item")!;
        indexer.SetValue(dict, inQueueEntry, ["sem-inqueue"]);

        var found = InvokeTryGetSemanticCachedEntry(handler, "sem-inqueue", out var entry);

        Assert.False(found);
        Assert.Null(entry);
    }

    // ── InQueue TTL is shorter than Ready TTL ────────────────────────────────

    [Fact]
    public void IsExpired_InQueueEntry_UsesInQueueTtl()
    {
        // Create a handler with InQueue TTL = 0 (immediate expiry) and hover TTL = 60s.
        // An InQueue entry stored at current time should be immediately expired.
        var handler = CreateHandlerWithTtls(hoverTtlMs: 60_000, inQueueTtlMs: 0);
        var (inQueueComputed, _) = MakeEntries(QueryTranslationStatus.InQueue);

        InvokeCacheEntry(handler, "k6", inQueueComputed, semanticContext: null);

        // With InQueue TTL = 0, the entry should be immediately expired and not retrievable.
        var found = InvokeTryGetCachedEntry(handler, "k6", out _);
        Assert.False(found);
    }

    [Fact]
    public void IsExpired_ReadyEntry_UsesHoverTtl()
    {
        // Create a handler with hover TTL = 60s and InQueue TTL = 0.
        // A Ready entry stored at current time should NOT be expired under the hover TTL.
        var handler = CreateHandlerWithTtls(hoverTtlMs: 60_000, inQueueTtlMs: 0);
        var (readyComputed, _) = MakeEntries(QueryTranslationStatus.Ready);

        InvokeCacheEntry(handler, "k7", readyComputed, semanticContext: null);

        var found = InvokeTryGetCachedEntry(handler, "k7", out var entry);
        Assert.True(found);
        Assert.Equal(QueryTranslationStatus.Ready, GetEntryStatus(entry!));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HoverHandler CreateHandler() =>
        new(new EFQueryLens.Lsp.DocumentManager(), new HoverPreviewService(new NoOpQueryLensEngine()));

    private static HoverHandler CreateHandlerWithTtls(int hoverTtlMs, int inQueueTtlMs)
    {
        // Override via env vars before construction; restore after.
        // Simpler: use reflection to set the private fields after construction.
        var handler = CreateHandler();
        SetField(handler, "_hoverCacheTtlMs", hoverTtlMs);
        SetField(handler, "_inQueueCacheTtlMs", inQueueTtlMs);
        return handler;
    }

    /// <summary>Produces a (ComputedEntry, CachedEntry) pair for a given status.</summary>
    private static (object computed, object cached) MakeEntries(QueryTranslationStatus status)
    {
        var hover = status is QueryTranslationStatus.Ready ? new Hover() : null;

        var structuredResult = status is QueryTranslationStatus.Ready
            ? new QueryLensStructuredHoverResult(
                Success: true,
                ErrorMessage: null,
                Statements: [],
                CommandCount: 1,
                SourceExpression: "db.X",
                ExecutedExpression: null,
                DbContextType: "AppDb",
                ProviderName: "sqlite",
                SourceFile: null,
                SourceLine: 1,
                Warnings: [],
                EnrichedSql: null,
                Mode: "direct",
                Status: QueryTranslationStatus.Ready,
                StatusMessage: null,
                AvgTranslationMs: 0)
            : new QueryLensStructuredHoverResult(
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
                Status: status,
                StatusMessage: "Computing...",
                AvgTranslationMs: 0);

        var computedType = typeof(HoverHandler).GetNestedType("ComputedEntry", BindingFlags.NonPublic)!;
        var computed = Activator.CreateInstance(computedType, hover, structuredResult, status)!;

        var cachedType = typeof(HoverHandler).GetNestedType("CachedEntry", BindingFlags.NonPublic)!;
        var cached = Activator.CreateInstance(cachedType, DateTime.UtcNow.Ticks, hover, structuredResult, status)!;

        return (computed, cached);
    }

    private static object CreateCachedEntry(long ticks, Hover? hover, object? structured, QueryTranslationStatus status)
    {
        var cachedType = typeof(HoverHandler).GetNestedType("CachedEntry", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(cachedType, ticks, hover, structured, status)!;
    }

    private static object CreateSemanticHoverContext(string key, int line, int character)
    {
        var type = typeof(HoverHandler).GetNestedType("SemanticHoverContext", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(type, key, line, character)!;
    }

    private static void InvokeCacheEntry(HoverHandler handler, string cacheKey, object computedEntry, object? semanticContext)
    {
        var method = typeof(HoverHandler).GetMethod("CacheEntry", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(handler, [cacheKey, computedEntry, semanticContext]);
    }

    private static bool InvokeTryGetCachedEntry(HoverHandler handler, string cacheKey, out object? entry)
    {
        var method = typeof(HoverHandler).GetMethod("TryGetCachedEntry", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var args = new object?[] { cacheKey, null };
        var result = (bool)method.Invoke(handler, args)!;
        entry = args[1];
        return result;
    }

    private static bool InvokeTryGetSemanticCachedEntry(HoverHandler handler, string semanticKey, out object? entry)
    {
        var method = typeof(HoverHandler).GetMethod("TryGetSemanticCachedEntry", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var args = new object?[] { semanticKey, null };
        var result = (bool)method.Invoke(handler, args)!;
        entry = args[1];
        return result;
    }

    private static int GetDictionaryCount(HoverHandler handler, string fieldName)
    {
        var field = typeof(HoverHandler).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = field.GetValue(handler)!;
        return (int)dict.GetType().GetProperty("Count")!.GetValue(dict)!;
    }

    private static TField GetField<TField>(HoverHandler handler, string fieldName)
    {
        var field = typeof(HoverHandler).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (TField)field.GetValue(handler)!;
    }

    private static QueryTranslationStatus GetEntryStatus(object entry)
    {
        var prop = entry.GetType().GetProperty("Status")!;
        return (QueryTranslationStatus)prop.GetValue(entry)!;
    }

    private static void SetField(HoverHandler handler, string fieldName, object value)
    {
        var field = typeof(HoverHandler).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(handler, value);
    }

    private sealed class NoOpQueryLensEngine : IQueryLensEngine
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<QueryTranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
            => Task.FromResult(new QueryTranslationResult());
        public Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ModelSnapshot { DbContextType = string.Empty });
    }
}
