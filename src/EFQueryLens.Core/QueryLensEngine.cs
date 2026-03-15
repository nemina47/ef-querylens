using System.Collections.Concurrent;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Scripting;

namespace EFQueryLens.Core;

/// <summary>
/// Default implementation of <see cref="IQueryLensEngine"/>.
///
/// Orchestrates the ALC cache, the Roslyn scripting evaluator, and EF Core
/// model inspection without ever opening a real database connection.
/// </summary>
public sealed partial class QueryLensEngine : IQueryLensEngine, IDbContextPoolProvider
{
    private sealed record CachedAssemblyContext(
        string SourceAssemblyPath,
        string SourceFingerprint,
        string ShadowAssemblyPath,
        ProjectAssemblyContext Context);

    private sealed record PooledDbContext(
        string PoolKey,
        string DbContextTypeFullName,
        object Instance,
        SemaphoreSlim Gate,
        string CreationStrategy);

    private sealed class DbContextLease : IDbContextLease
    {
        private readonly QueryLensEngine _owner;
        private readonly PooledDbContext _pooled;
        private bool _disposed;

        public DbContextLease(QueryLensEngine owner, PooledDbContext pooled, string strategy)
        {
            _owner = owner;
            _pooled = pooled;
            Strategy = strategy;
        }

        public object Instance => _pooled.Instance;

        public string PoolKey => _pooled.PoolKey;

        public string Strategy { get; }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _owner.ReleaseDbContextLease(_pooled);
            return ValueTask.CompletedTask;
        }
    }

    private readonly QueryEvaluator _evaluator = new();
    private readonly ConcurrentDictionary<string, CachedAssemblyContext> _alcCache = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _alcContextGates = new(
        StringComparer.OrdinalIgnoreCase);
    
    // DbContext pool: keyed by "assemblyPath|dbContextTypeFullName" for isolation
    private readonly ConcurrentDictionary<string, PooledDbContext> _dbContextPool = new(
        StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _dbContextCreateGates = new(
        StringComparer.Ordinal);
    
    private readonly bool _debugEnabled;
    private readonly ShadowAssemblyCache _shadowCache;
    private bool _disposed;

    public QueryLensEngine()
    {
        _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);
        _shadowCache = new ShadowAssemblyCache(_debugEnabled);
        _shadowCache.ScheduleCleanupIfDue(force: true);
    }

    // ── IQueryLensEngine ──────────────────────────────────────────────────────

    public async Task<QueryTranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var fullPath = Path.GetFullPath(request.AssemblyPath);
        var alcCtx = GetOrRefreshContext(fullPath);
        var result = await _evaluator.EvaluateAsync(alcCtx, request, ct, this, fullPath);
        LogTranslationTiming(fullPath, result);

        if (!NeedsDbContextDiscoveryRetry(result))
            return result;

        // Self-heal long-running hosts (LSP): evict the cached context and retry once.
        // This recovers from contexts created before dependency outputs existed or from
        // transient assembly-load failures during initial discovery.
        if (_alcCache.TryRemove(fullPath, out var stale))
            ReleaseCachedContext(stale, reason: "retry");

        var freshCtx = GetOrRefreshContext(fullPath);
        var retryResult = await _evaluator.EvaluateAsync(freshCtx, request, ct, this, fullPath);
        LogTranslationTiming(fullPath, retryResult);
        return retryResult;
    }

    public async Task<QueuedTranslationResult> TranslateQueuedAsync(
        TranslationRequest request, CancellationToken ct = default)
    {
        var result = await TranslateAsync(request, ct);
        return new QueuedTranslationResult
        {
            Status = QueryTranslationStatus.Ready,
            AverageTranslationMs = 0,
            Result = result,
        };
    }

    public Task<ExplainResult> ExplainAsync(
        ExplainRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException(
            "ExplainAsync requires a live database connection and is implemented in Phase 2.");

    public async Task<ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        LogDebug($"inspect-model-start assembly={request.AssemblyPath}");

        try
        {
            var assemblyPath = Path.GetFullPath(request.AssemblyPath);
            var alcCtx = GetOrRefreshContext(assemblyPath);
            var dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName);
            var dbInstance = CreateDbContextForInspection(dbContextType, alcCtx);

            var snapshot = BuildModelSnapshot(dbInstance, dbContextType);
            LogDebug($"inspect-model-success context={snapshot.DbContextType} dbSets={snapshot.DbSetProperties.Count} entities={snapshot.Entities.Count}");
            return snapshot;
        }
        catch (Exception ex)
        {
            LogDebug($"inspect-model-failure type={ex.GetType().Name} message={ex.Message}");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _alcCache.Values)
            ReleaseCachedContext(entry, reason: "dispose");
        _alcCache.Clear();
        _alcContextGates.Clear();
        await DisposeDbContextPoolAsync();
        _shadowCache.CleanupIfDue(force: true);
    }
}
