using EFQueryLens.Core.AssemblyContext;

namespace EFQueryLens.Core;

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
                ReleaseCachedContext(existing, reason: "stale");
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

    private string BuildSourceFingerprint(string sourceAssemblyPath)
    {
        var info = new FileInfo(sourceAssemblyPath);
        return $"{Path.GetFullPath(sourceAssemblyPath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private void ReleaseCachedContext(CachedAssemblyContext entry, string reason)
    {
        _evaluator.InvalidateMetadataRefCache(entry.ShadowAssemblyPath);
        EvictPooledDbContextsForAssembly(entry.SourceAssemblyPath);
        entry.Context.Dispose();
        ForceUnloadCollection();

        LogDebug($"alc-release reason={reason} source={entry.SourceAssemblyPath} shadow={entry.ShadowAssemblyPath}");
    }

    private static void ForceUnloadCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    private void LogTranslationTiming(string assemblyPath, QueryTranslationResult result)
    {
        if (!_debugEnabled || result.Metadata is null)
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
            $"runnerMs={m.RunnerExecutionTime?.TotalMilliseconds:F0} " +
            $"fallbackMs={m.ToQueryStringFallbackTime?.TotalMilliseconds:F0}");
    }

    private static bool NeedsDbContextDiscoveryRetry(QueryTranslationResult result) =>
        !result.Success &&
        !string.IsNullOrWhiteSpace(result.ErrorMessage) &&
        result.ErrorMessage.Contains("No DbContext subclass found", StringComparison.OrdinalIgnoreCase);

    private static bool ReadBoolEnvironmentVariable(string variableName, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private void LogDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-Engine] {message}");
    }
}
