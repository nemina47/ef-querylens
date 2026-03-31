using System.Reflection;
using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Core.Tests.Lsp;

/// <summary>
/// Verifies Phase 3 of the hover cache redesign: non-blocking hover behaviour.
///   - TryCacheEntryInQueue race guard — first call wins, second call on same key loses.
///   - InQueue entry is immediately retrievable after the race guard write.
///   - TTL=0 (caching disabled) makes TryCacheEntryInQueue return false.
///   - When the engine fails (DaemonUnavailable), the InQueue placeholder is not replaced
///     because CacheEntry ignores non-Ready/non-InQueue statuses; recovery is via InQueue TTL.
///   - BuildInQueueStructured shape: Status==InQueue, Success==false, non-null StatusMessage.
/// </summary>
public class HoverHandlerNonBlockingTests
{
    // ── TryCacheEntryInQueue race guard ──────────────────────────────────────

    [Fact]
    public void TryCacheEntryInQueue_FirstCall_ReturnsTrue()
    {
        var handler = CreateHandler();
        var entry = BuildInQueueComputedEntry();

        var won = InvokeTryCacheEntryInQueue(handler, "k1", entry);

        Assert.True(won);
    }

    [Fact]
    public void TryCacheEntryInQueue_SecondCallSameKey_ReturnsFalse()
    {
        var handler = CreateHandler();
        var entry = BuildInQueueComputedEntry();

        InvokeTryCacheEntryInQueue(handler, "k2", entry);
        var secondWon = InvokeTryCacheEntryInQueue(handler, "k2", entry);

        Assert.False(secondWon);
    }

    [Fact]
    public void TryCacheEntryInQueue_DifferentKeys_BothReturnTrue()
    {
        var handler = CreateHandler();
        var entry = BuildInQueueComputedEntry();

        var first = InvokeTryCacheEntryInQueue(handler, "kA", entry);
        var second = InvokeTryCacheEntryInQueue(handler, "kB", entry);

        Assert.True(first);
        Assert.True(second);
    }

    // ── InQueue entry is retrievable immediately after enqueue ───────────────

    [Fact]
    public void TryCacheEntryInQueue_EntryIsRetrievableAsInQueueFromPrimaryCache()
    {
        var handler = CreateHandler();
        var entry = BuildInQueueComputedEntry();

        InvokeTryCacheEntryInQueue(handler, "k3", entry);

        Assert.True(InvokeTryGetCachedEntry(handler, "k3", out var retrieved));
        Assert.NotNull(retrieved);
        Assert.Equal(QueryTranslationStatus.InQueue, GetEntryStatus(retrieved!));
    }

    // ── TTL=0 disables caching ───────────────────────────────────────────────

    [Fact]
    public void TryCacheEntryInQueue_WhenCacheTtlIsZero_ReturnsFalse()
    {
        var handler = CreateHandler();
        SetField(handler, "_hoverCacheTtlMs", 0);

        var entry = BuildInQueueComputedEntry();
        var won = InvokeTryCacheEntryInQueue(handler, "k4", entry);

        Assert.False(won);
    }

    // ── Background compute replaces InQueue placeholder ─────────────────────

    /// <summary>
    /// Source text that contains no LINQ expression causes the pipeline to short-circuit
    /// with a Ready result (Success=false, "Could not extract...").  ComputeCombinedAsync
    /// normalises that to (null hover, null structured, Ready).  CacheEntry stores Ready
    /// entries, so the InQueue placeholder is replaced by the Ready entry.
    /// </summary>
    [Fact]
    public async Task BackgroundComputeAndCacheAsync_WhenNoLinqInSource_ReplacesInQueueWithReady()
    {
        var handler = CreateHandlerWithEngine(new NoOpQueryLensEngine());

        var entry = BuildInQueueComputedEntry();
        InvokeTryCacheEntryInQueue(handler, "k5", entry);

        // "src" contains no LINQ expression — pipeline resolves immediately without
        // calling the engine, returning Ready (no query found at this position).
        await InvokeBackgroundComputeAndCacheAsync(handler, "k5", "file.cs", "src", 1, 1, null);

        Assert.True(InvokeTryGetCachedEntry(handler, "k5", out var replaced));
        Assert.Equal(QueryTranslationStatus.Ready, GetEntryStatus(replaced!));
    }

    // ── BuildInQueueStructured shape ─────────────────────────────────────────

    [Fact]
    public void BuildInQueueStructured_StatusIsInQueue()
    {
        var result = HoverHandler.BuildInQueueStructured();

        Assert.Equal(QueryTranslationStatus.InQueue, result.Status);
    }

    [Fact]
    public void BuildInQueueStructured_SuccessIsFalse()
    {
        var result = HoverHandler.BuildInQueueStructured();

        Assert.False(result.Success);
    }

    [Fact]
    public void BuildInQueueStructured_HasNonNullStatusMessage()
    {
        var result = HoverHandler.BuildInQueueStructured();

        Assert.False(string.IsNullOrWhiteSpace(result.StatusMessage));
    }

    [Fact]
    public void BuildInQueueStructured_CommandCountIsZero()
    {
        var result = HoverHandler.BuildInQueueStructured();

        Assert.Equal(0, result.CommandCount);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HoverHandler CreateHandler() =>
        new(new EFQueryLens.Lsp.DocumentManager(), new HoverPreviewService(new NoOpQueryLensEngine()));

    private static HoverHandler CreateHandlerWithEngine(IQueryLensEngine engine) =>
        new(new EFQueryLens.Lsp.DocumentManager(), new HoverPreviewService(engine));

    private static object BuildInQueueComputedEntry()
    {
        var computedType = typeof(HoverHandler).GetNestedType("ComputedEntry", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(
            computedType,
            (Hover?)null,
            HoverHandler.BuildInQueueStructured(),
            QueryTranslationStatus.InQueue)!;
    }

    private static bool InvokeTryCacheEntryInQueue(HoverHandler handler, string cacheKey, object entry)
    {
        var method = typeof(HoverHandler)
            .GetMethod("TryCacheEntryInQueue", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (bool)method.Invoke(handler, [cacheKey, entry])!;
    }

    private static bool InvokeTryGetCachedEntry(HoverHandler handler, string cacheKey, out object? entry)
    {
        var method = typeof(HoverHandler)
            .GetMethod("TryGetCachedEntry", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var args = new object?[] { cacheKey, null };
        var result = (bool)method.Invoke(handler, args)!;
        entry = args[1];
        return result;
    }

    private static Task InvokeBackgroundComputeAndCacheAsync(
        HoverHandler handler,
        string cacheKey,
        string filePath,
        string sourceText,
        int line,
        int character,
        object? semanticContext)
    {
        var method = typeof(HoverHandler)
            .GetMethod("BackgroundComputeAndCacheAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(handler, [cacheKey, filePath, sourceText, line, character, semanticContext])!;
    }

    private static QueryTranslationStatus GetEntryStatus(object entry)
    {
        var prop = entry.GetType().GetProperty("Status")!;
        return (QueryTranslationStatus)prop.GetValue(entry)!;
    }

    private static void SetField(HoverHandler handler, string fieldName, object value)
    {
        var field = typeof(HoverHandler)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(handler, value);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>Engine that always throws — simulates a daemon crash.</summary>
    private sealed class ThrowingEngine : IQueryLensEngine
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<QueryTranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
            => Task.FromException<QueryTranslationResult>(new InvalidOperationException("daemon offline"));

        public Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ModelSnapshot { DbContextType = string.Empty });

        public Task<FactoryGenerationResult> GenerateFactoryAsync(FactoryGenerationRequest request, CancellationToken ct = default)
            => Task.FromException<FactoryGenerationResult>(new NotSupportedException());
    }

    private sealed class NoOpQueryLensEngine : IQueryLensEngine
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<QueryTranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
            => Task.FromResult(new QueryTranslationResult());

        public Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ModelSnapshot { DbContextType = string.Empty });

        public Task<FactoryGenerationResult> GenerateFactoryAsync(FactoryGenerationRequest request, CancellationToken ct = default)
            => Task.FromException<FactoryGenerationResult>(new NotSupportedException());
    }
}
