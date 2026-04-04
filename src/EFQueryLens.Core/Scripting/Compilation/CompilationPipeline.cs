using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EFQueryLens.Core.Scripting.Evaluation;

internal sealed partial class CompilationPipeline
{
    private sealed record MetadataRefEntry(MetadataReference[] Refs, long LastAccessTicks);
    private const int MaxMetadataRefCacheEntries = 64;

    private readonly ConcurrentDictionary<string, MetadataRefEntry> _refCache = new();
    private readonly INamespaceTypeIndexCache _namespaceTypeIndexCache;
    private readonly bool _debugEnabled;
    private readonly bool _dumpSourceEnabled;

    internal CompilationPipeline(
        INamespaceTypeIndexCache namespaceTypeIndexCache,
        bool debugEnabled,
        bool dumpSourceEnabled)
    {
        _namespaceTypeIndexCache = namespaceTypeIndexCache;
        _debugEnabled = debugEnabled;
        _dumpSourceEnabled = dumpSourceEnabled;
    }

    private void LogDebug(string message)
    {
        if (_debugEnabled)
            Console.Error.WriteLine($"[QL-Eval] {message}");
    }

    private void TouchMetadataRefCacheEntry(string key, MetadataRefEntry entry)
    {
        _refCache.TryUpdate(
            key,
            entry with { LastAccessTicks = QueryEvaluator.GetUtcNowTicks() },
            entry);
    }

    internal (HashSet<string> Namespaces, HashSet<string> Types) GetOrBuildNamespaceTypeIndex(
        string namespaceIndexCacheKey,
        IReadOnlyList<Assembly> compilationAssemblies)
    {
        var filteredAssemblies = compilationAssemblies
            .Where(a =>
            {
                var loc = a.Location;
                if (string.IsNullOrEmpty(loc)) return false;
                var normalizedLoc = loc.Replace('\\', '/');
                if (normalizedLoc.Contains("/runtimes/", StringComparison.OrdinalIgnoreCase)) return false;
                var name = a.GetName().Name;
                if (name is not null && name.StartsWith("__QueryLensEval_", StringComparison.Ordinal)) return false;
                return !ShouldSkipMetadataReferenceAssembly(name);
            })
            .ToList();

        var currentFingerprint = namespaceIndexCacheKey;
        var lookupWatch = System.Diagnostics.Stopwatch.StartNew();
        if (_namespaceTypeIndexCache.TryGet(
            namespaceIndexCacheKey,
            currentFingerprint,
            out var cachedNamespaces,
            out var cachedTypes))
        {
            lookupWatch.Stop();
            var metrics = _namespaceTypeIndexCache.GetMetrics();
            Console.Error.WriteLine(
                $"[QL-Engine] namespace-index cache=hit lookupMs={lookupWatch.Elapsed.TotalMilliseconds:0.###} " +
                $"hits={metrics.Hits} misses={metrics.Misses} count={metrics.Count}");
            return (cachedNamespaces, cachedTypes);
        }
        lookupWatch.Stop();

        var buildWatch = System.Diagnostics.Stopwatch.StartNew();
        var result = ImportResolver.BuildKnownNamespaceAndTypeIndex(filteredAssemblies);
        buildWatch.Stop();
        _namespaceTypeIndexCache.Set(namespaceIndexCacheKey, currentFingerprint, result.Namespaces, result.Types);
        var metricsAfterBuild = _namespaceTypeIndexCache.GetMetrics();
        Console.Error.WriteLine(
            $"[QL-Engine] namespace-index cache=miss lookupMs={lookupWatch.Elapsed.TotalMilliseconds:0.###} " +
            $"buildMs={buildWatch.Elapsed.TotalMilliseconds:0.###} hits={metricsAfterBuild.Hits} " +
            $"misses={metricsAfterBuild.Misses} count={metricsAfterBuild.Count}");
        return result;
    }

    internal QueryTranslationResult? TryBuildCompilationWithRetries(
        TranslationRequest request,
        Type dbContextType,
        IReadOnlyList<Assembly> compilationAssemblies,
        MetadataReference[] refs,
        HashSet<string> knownNamespaces,
        HashSet<string> knownTypes,
        List<string> stubs,
        HashSet<string> synthesizedUsingStaticTypes,
        HashSet<string> synthesizedUsingNamespaces,
        TimeSpan elapsed,
        IEnumerable<Assembly>? loadedAssemblies,
        CancellationToken ct,
        ref string workingExpression,
        ref int compilationRetryCount,
        out CSharpCompilation compilation)
    {
        var maxRetries = 5;
        var lastSrc = string.Empty;

        string DumpSrcToTemp()
        {
            return QueryEvaluator.DumpGeneratedSourceToTemp(lastSrc);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var workingRequest = request with { Expression = workingExpression };

            var src = EvalSourceBuilder.BuildEvalSource(
                dbContextType,
                workingRequest,
                stubs,
                knownNamespaces,
                knownTypes,
                synthesizedUsingStaticTypes,
                synthesizedUsingNamespaces);
            lastSrc = src;

            if (_dumpSourceEnabled)
            {
                var dumpPath = QueryEvaluator.DumpGeneratedSourceToTemp(src);
                LogDebug($"generated-source dump={dumpPath}");
            }

            compilation = EvalSourceBuilder.BuildCompilation(src, refs);
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            var hardErrors = errors.Where(e => e.Id is not ("CS1061" or "CS1929" or "CS7036" or "CS0019" or "CS8122" or "CS0246" or "CS0234" or "CS0400" or "CS1503"
                or "CS0122")).ToList();
            if (hardErrors.Count > 0)
            {
                return QueryEvaluator.FailureFromDiagnostics(
                    stage: "Compilation error",
                    errors: hardErrors,
                    elapsed: elapsed,
                    dbContextType: dbContextType,
                    userAssemblies: loadedAssemblies,
                    softDiagnostics: false,
                    sourceDumpPath: DumpSrcToTemp());
            }

            if (errors.Count == 0)
            {
                return null;
            }

            if (maxRetries-- <= 0)
            {
                return QueryEvaluator.FailureFromDiagnostics(
                    stage: "Compilation error",
                    errors: errors,
                    elapsed: elapsed,
                    dbContextType: dbContextType,
                    userAssemblies: loadedAssemblies,
                    softDiagnostics: true,
                    sourceDumpPath: DumpSrcToTemp());
            }

            compilationRetryCount++;

            LogDebug($"compile-retry iteration={compilationRetryCount} errorCount={errors.Count}");
            foreach (var err in errors.Take(10))
            {
                LogDebug($"  diagnostic id={err.Id} msg={err.GetMessage()}");
            }

            var changed = ApplyCompileRetryAdjustments(
                errors,
                compilation,
                compilationAssemblies,
                knownNamespaces,
                knownTypes,
                stubs,
                synthesizedUsingStaticTypes,
                synthesizedUsingNamespaces);

            if (TryNormalizeInaccessibleProjectionTypeFromErrors(errors, workingExpression, out var normalizedExpression)
                && !string.Equals(normalizedExpression, workingExpression, StringComparison.Ordinal))
            {
                workingExpression = normalizedExpression;
                LogDebug($"compile-retry iteration={compilationRetryCount} normalize-inaccessible-projection applied");
                changed = true;
            }

            if (!changed)
            {
                LogDebug($"compile-retry iteration={compilationRetryCount} no-changes-made");
                if (errors.All(e => e.Id == "CS0122"))
                {
                    return null;
                }

                return QueryEvaluator.FailureFromDiagnostics(
                    stage: "Compilation error",
                    errors: errors,
                    elapsed: elapsed,
                    dbContextType: dbContextType,
                    userAssemblies: loadedAssemblies,
                    softDiagnostics: true,
                    sourceDumpPath: DumpSrcToTemp());
            }
        }
    }
}
