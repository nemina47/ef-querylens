using System.Collections.Concurrent;
using System.Reflection;
using EFQueryLens.Core.Scripting;

namespace EFQueryLens.Core;

public sealed partial class QueryLensEngine
{
    // DbContext pool
    async ValueTask<IDbContextLease> IDbContextPoolProvider.AcquireDbContextLeaseAsync(
        Type dbContextType,
        string assemblyPath,
        IEnumerable<Assembly> userAssemblies,
        CancellationToken cancellationToken)
    {
        var poolKey = BuildDbContextPoolKey(assemblyPath, dbContextType);
        var createdNow = false;

        if (!_dbContextPool.TryGetValue(poolKey, out var pooled))
        {
            var createGate = _dbContextCreateGates.GetOrAdd(poolKey, static _ => new SemaphoreSlim(1, 1));
            await createGate.WaitAsync(cancellationToken);
            try
            {
                if (!_dbContextPool.TryGetValue(poolKey, out pooled))
                {
                    var (instance, strategy) = QueryEvaluator.CreateDbContextInstance(dbContextType, userAssemblies);
                    pooled = new PooledDbContext(
                        poolKey,
                        dbContextType.FullName!,
                        instance,
                        new SemaphoreSlim(1, 1),
                        strategy);
                    _dbContextPool[poolKey] = pooled;
                    createdNow = true;

                    if (_debugEnabled)
                    {
                        LogDebug($"dbcontext-pool-create type={dbContextType.Name} strategy={strategy}");
                    }
                }
            }
            finally
            {
                createGate.Release();
            }
        }

        await pooled.Gate.WaitAsync(cancellationToken);

        var leaseStrategy = createdNow ? pooled.CreationStrategy : "pooled-reuse";
        if (_debugEnabled)
        {
            LogDebug($"dbcontext-pool-lease-acquired type={dbContextType.Name} strategy={leaseStrategy}");
        }

        return new DbContextLease(this, pooled, leaseStrategy);
    }

    private void ReleaseDbContextLease(PooledDbContext pooled)
    {
        try
        {
            ClearChangeTracker(pooled.Instance);

            if (_debugEnabled)
            {
                LogDebug($"dbcontext-pool-lease-released type={pooled.DbContextTypeFullName}");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"dbcontext-pool-clear-error type={pooled.DbContextTypeFullName} error={ex.GetType().Name} message={ex.Message}");
        }
        finally
        {
            pooled.Gate.Release();
        }
    }

    private static void ClearChangeTracker(object dbContextInstance)
    {
        var changeTrackerProp = dbContextInstance.GetType()
            .GetProperty("ChangeTracker", BindingFlags.Public | BindingFlags.Instance);

        if (changeTrackerProp?.GetValue(dbContextInstance) is not { } changeTracker)
        {
            return;
        }

        var clearMethod = changeTracker.GetType()
            .GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
        clearMethod?.Invoke(changeTracker, null);
    }

    private string BuildDbContextPoolKey(string assemblyPath, Type dbContextType)
    {
        return $"{Path.GetFullPath(assemblyPath)}|{dbContextType.FullName}";
    }

    private async ValueTask DisposeDbContextPoolAsync()
    {
        if (_debugEnabled && _dbContextPool.Count > 0)
        {
            LogDebug($"dbcontext-pool-dispose count={_dbContextPool.Count}");
        }

        foreach (var pooled in _dbContextPool.Values)
        {
            try
            {
                pooled.Gate.Dispose();

                if (pooled.Instance is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (pooled.Instance is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"dbcontext-pool-dispose-error type={pooled.DbContextTypeFullName} error={ex.GetType().Name} message={ex.Message}");
            }
        }

        _dbContextPool.Clear();

        foreach (var gate in _dbContextCreateGates.Values)
        {
            gate.Dispose();
        }

        _dbContextCreateGates.Clear();
    }

    private void EvictPooledDbContextsForAssembly(string sourceAssemblyPath)
    {
        var fullPath = Path.GetFullPath(sourceAssemblyPath);
        var prefix = fullPath + "|";
        var keys = _dbContextPool.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

        foreach (var key in keys)
        {
            if (_dbContextPool.TryRemove(key, out var pooled))
            {
                try
                {
                    pooled.Gate.Dispose();

                    if (pooled.Instance is IAsyncDisposable asyncDisposable)
                    {
                        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    else if (pooled.Instance is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"dbcontext-pool-evict-dispose-error key={key} error={ex.GetType().Name} message={ex.Message}");
                }
            }

            if (_dbContextCreateGates.TryRemove(key, out var createGate))
            {
                createGate.Dispose();
            }
        }

        if (_debugEnabled && keys.Length > 0)
        {
            LogDebug($"dbcontext-pool-evict assembly={fullPath} removed={keys.Length}");
        }
    }
}
