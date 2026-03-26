using System.Collections.Concurrent;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Common;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting;
using QueryEvaluator = EFQueryLens.Core.Scripting.Evaluation.QueryEvaluator;

namespace EFQueryLens.Core.Engine;

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

    private sealed class PooledDbContextPool(
        string poolKey,
        string dbContextTypeFullName,
        int poolSize)
    {
        public string PoolKey { get; } = poolKey;

        public string DbContextTypeFullName { get; } = dbContextTypeFullName;

        public int PoolSize { get; } = poolSize;

        public SemaphoreSlim Gate { get; } = new(poolSize, poolSize);

        public object CreateLock { get; } = new();

        public ConcurrentQueue<object> AvailableInstances { get; } = new();

        public ConcurrentBag<object> CreatedInstances { get; } = [];

        public int CreatedCount;
    }

    private sealed class CreateGateState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ActiveUsers;
    }

    private sealed class DbContextLease(
        QueryLensEngine owner,
        PooledDbContextPool pool,
        object instance,
        string strategy)
        : IDbContextLease
    {
        private bool _disposed;

        public object Instance => instance;

        public string PoolKey => pool.PoolKey;

        public string Strategy { get; } = strategy;

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            owner.ReleaseDbContextLease(pool, instance);
            return ValueTask.CompletedTask;
        }
    }

    private readonly QueryEvaluator _evaluator = new();
    private readonly ConcurrentDictionary<string, CachedAssemblyContext> _alcCache = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _alcContextGates = new(
        StringComparer.OrdinalIgnoreCase);
    
    // DbContext pool: keyed by "assemblyPath|dbContextTypeFullName" for isolation
    private readonly ConcurrentDictionary<string, PooledDbContextPool> _dbContextPool = new(
        StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CreateGateState> _dbContextCreateGates = new(
        StringComparer.Ordinal);
    
    private readonly bool _debugEnabled;
    private readonly int _dbContextPoolSize;
    private readonly ShadowAssemblyCache _shadowCache;
    private bool _disposed;

    public QueryLensEngine()
    {
        _debugEnabled = EnvironmentVariableParser.ReadBool("QUERYLENS_DEBUG", fallback: false);
        _dbContextPoolSize = EnvironmentVariableParser.ReadInt(
            "QUERYLENS_DBCONTEXT_POOL_SIZE",
            fallback: 4,
            min: 1,
            max: 16);
        _shadowCache = new ShadowAssemblyCache(_debugEnabled);
        _shadowCache.RunStartupCleanup();
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
            await ReleaseCachedContextAsync(stale, reason: "retry");

        var freshCtx = GetOrRefreshContext(fullPath);
        var retryResult = await _evaluator.EvaluateAsync(freshCtx, request, ct, this, fullPath);
        LogTranslationTiming(fullPath, retryResult);
        return retryResult;
    }

    public Task<ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request, CancellationToken ct = default)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(request);

            LogDebug($"inspect-model-start assembly={request.AssemblyPath}");

            try
            {
                var assemblyPath = Path.GetFullPath(request.AssemblyPath);
                var alcCtx = GetOrRefreshContext(assemblyPath);
                Type dbContextType;
                try
                {
                    dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName);
                }
                catch (InvalidOperationException ex) when (QueryEvaluator.IsNoDbContextFoundError(ex))
                {
                    QueryEvaluator.TryLoadSiblingAssemblies(alcCtx);
                    dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName);
                }
                var dbInstance = CreateDbContextForInspection(dbContextType, alcCtx);

                var snapshot = BuildModelSnapshot(dbInstance, dbContextType);
                LogDebug($"inspect-model-success context={snapshot.DbContextType} dbSets={snapshot.DbSetProperties.Count} entities={snapshot.Entities.Count}");
                return Task.FromResult(snapshot);
            }
            catch (Exception ex)
            {
                LogDebug($"inspect-model-failure type={ex.GetType().Name} message={ex.Message}");
                throw;
            }
        }
        catch (Exception exception)
        {
            return Task.FromException<ModelSnapshot>(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _alcCache.Values)
            await ReleaseCachedContextAsync(entry, reason: "dispose");
        _alcCache.Clear();
        _alcContextGates.Clear();
        await DisposeDbContextPoolAsync();
    }
}
