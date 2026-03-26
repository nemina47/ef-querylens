using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Engine;

public sealed partial class QueryLensEngine
{
    // ALC cache
    private ProjectAssemblyContext GetOrRefreshContext(string assemblyPath)
    {
        var sourceAssemblyPath = Path.GetFullPath(assemblyPath);
        var gate = _alcContextGates.GetOrAdd(sourceAssemblyPath, static _ => new object());
        lock (gate)
        {
            var sourceFingerprint = BuildSourceFingerprint(sourceAssemblyPath);
            var shadowAssemblyPath = _shadowCache.ResolveOrCreateBundle(sourceAssemblyPath);

            if (_alcCache.TryGetValue(sourceAssemblyPath, out var existing))
            {
                if (string.Equals(existing.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal)
                    && string.Equals(existing.ShadowAssemblyPath, shadowAssemblyPath, StringComparison.OrdinalIgnoreCase)
                    && !ProjectAssemblyContextFactory.IsStale(existing.Context))
                {
                    return existing.Context;
                }

                _alcCache.TryRemove(sourceAssemblyPath, out _);
                QueueReleaseCachedContext(existing, reason: "stale");
            }

            var freshContext = ProjectAssemblyContextFactory.Create(shadowAssemblyPath);
            var cachedContext = new CachedAssemblyContext(
                sourceAssemblyPath,
                sourceFingerprint,
                shadowAssemblyPath,
                freshContext);

            _alcCache[sourceAssemblyPath] = cachedContext;
            return freshContext;
        }
    }

    private static string BuildSourceFingerprint(string sourceAssemblyPath)
    {
        var info = new FileInfo(sourceAssemblyPath);
        return $"{Path.GetFullPath(sourceAssemblyPath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private void QueueReleaseCachedContext(CachedAssemblyContext entry, string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ReleaseCachedContextAsync(entry, reason);
            }
            catch (Exception ex)
            {
                LogDebug($"alc-release-failure reason={reason} source={entry.SourceAssemblyPath} error={ex.GetType().Name} message={ex.Message}");
            }
        });
    }

    private async ValueTask ReleaseCachedContextAsync(CachedAssemblyContext entry, string reason)
    {
        _evaluator.InvalidateMetadataRefCache(entry.ShadowAssemblyPath);
        await EvictPooledDbContextsForAssemblyAsync(entry.SourceAssemblyPath);
        entry.Context.Dispose();
        ForceUnloadCollection(aggressive: string.Equals(reason, "dispose", StringComparison.Ordinal));

        if (!_alcCache.ContainsKey(entry.SourceAssemblyPath))
        {
            _alcContextGates.TryRemove(entry.SourceAssemblyPath, out _);
        }

        LogDebug($"alc-release reason={reason} source={entry.SourceAssemblyPath} shadow={entry.ShadowAssemblyPath}");
    }

    private static void ForceUnloadCollection(bool aggressive)
    {
        if (!aggressive)
        {
            // Routine cache refreshes should avoid stop-the-world finalizer waits.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
            return;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    private void LogTranslationTiming(string assemblyPath, QueryTranslationResult result)
    {
        if (!_debugEnabled)
        {
            return;
        }

        var m = result.Metadata;
        LogDebug(
            "translate-timing " +
            $"assembly={assemblyPath} success={result.Success} " +
            $"totalMs={m.TranslationTime.TotalMilliseconds:F0} " +
            $"contextMs={m.ContextResolutionTime?.TotalMilliseconds:F0} " +
            $"dbContextMs={m.DbContextCreationTime?.TotalMilliseconds:F0} " +
            $"refsMs={m.MetadataReferenceBuildTime?.TotalMilliseconds:F0} " +
            $"compileMs={m.RoslynCompilationTime?.TotalMilliseconds:F0} " +
            $"retries={m.CompilationRetryCount} " +
            $"evalLoadMs={m.EvalAssemblyLoadTime?.TotalMilliseconds:F0} " +
            $"runnerMs={m.RunnerExecutionTime?.TotalMilliseconds:F0}");
    }

    private static bool NeedsDbContextDiscoveryRetry(QueryTranslationResult result) =>
        !result.Success &&
        !string.IsNullOrWhiteSpace(result.ErrorMessage) &&
        result.ErrorMessage.Contains("No DbContext subclass found", StringComparison.OrdinalIgnoreCase);

    private void LogDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-Engine] {message}");
    }
}
