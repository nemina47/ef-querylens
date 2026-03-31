using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EFQueryLens.Core.Scripting.Evaluation;

/// <summary>
/// Evaluates a LINQ expression string against an offline <c>DbContext</c> instance
/// loaded via <see cref="ProjectAssemblyContext"/> and returns the captured SQL commands
/// as a <see cref="QueryTranslationResult"/>.
///
/// <para>
/// The expression is compiled by Roslyn (<see cref="CSharpCompilation"/>) into a small
/// in-memory assembly that is loaded into the user's own isolated
/// <see cref="AssemblyLoadContext"/> via <see cref="ProjectAssemblyContext.LoadEvalAssembly"/>.
/// The cast to the concrete DbContext type and all EF Core calls therefore execute in the
/// same ALC as the user's assemblies, so any EF Core major version is supported without
/// cross-version type-identity conflicts.
/// </para>
///
/// <para>
/// SQL is captured by installing a generated offline connection on the DbContext
/// before execution. The generated command stubs intercept every
/// <c>DbCommand.Execute*</c> call, record SQL + parameters into a generated
/// <c>AsyncLocal</c>-based capture scope, and return a generated fake data reader
/// so EF Core materialization completes without a real database.
/// </para>
///
/// No real database connection is ever opened.
/// </summary>
public sealed partial class QueryEvaluator
{
    private const int MaxMetadataRefCacheEntries = 64;
    private const int MaxEvalRunnerCacheEntries = 256;
    private const int MaxNamespaceTypeIndexCacheEntries = 64;

    // Building MetadataReference objects from disk is expensive (100-500 ms for a
    // large project). Cache them keyed on shadow path + last-write timestamp +
    // assembly-set hash — the fingerprint changes on every rebuild, so stale entries
    // expire naturally via LRU without explicit eviction.
    private sealed record MetadataRefEntry(MetadataReference[] Refs, long LastAccessTicks);

    private readonly ConcurrentDictionary<string, MetadataRefEntry> _refCache = new();

    private delegate object? SyncRunnerInvoker(object dbInstance);
    private delegate Task<object?> AsyncRunnerInvoker(object dbInstance, CancellationToken ct);

    // Compiled + loaded eval runner cache: skip the entire Roslyn pipeline on warm hits.
    // Keys follow the pattern: "shadowAssemblyPath|timestampTicks|assemblySetHash|dbContextTypeName|requestHash"
    // Evicted whenever the ALC for a shadow assembly is released (InvalidateMetadataRefCache).
    private sealed record EvalRunnerEntry(
        Assembly EvalAssembly,
        SyncRunnerInvoker? SyncInvoker,
        AsyncRunnerInvoker? AsyncInvoker,
        long LastAccessTicks,
        string? ExecutedExpression = null);
    private readonly ConcurrentDictionary<string, EvalRunnerEntry> _evalRunnerCache = new(StringComparer.Ordinal);

    private readonly INamespaceTypeIndexCache _namespaceTypeIndexCache;

    private readonly bool _debugEnabled = 
        EFQueryLens.Core.Common.EnvironmentVariableParser.ReadBool("QUERYLENS_DEBUG", fallback: false);

    private readonly bool _dumpSourceEnabled =
        EFQueryLens.Core.Common.EnvironmentVariableParser.ReadBool("QUERYLENS_DUMP_SOURCE", fallback: false);

    public QueryEvaluator(INamespaceTypeIndexCache? namespaceTypeIndexCache = null)
    {
        _namespaceTypeIndexCache = namespaceTypeIndexCache ?? new NamespaceTypeIndexCache(MaxNamespaceTypeIndexCacheEntries);
    }

    internal sealed record EvaluationStageTimings(
        TimeSpan? ContextResolution,
        TimeSpan? DbContextCreation,
        TimeSpan? MetadataReferenceBuild,
        TimeSpan? RoslynCompilation,
        int CompilationRetryCount,
        TimeSpan? EvalAssemblyLoad,
        TimeSpan? RunnerExecution);

    internal void InvalidateMetadataRefCache(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
            return;

        // Only the eval runner cache needs explicit eviction — its entries hold Assembly
        // references that would prevent the collectible ALC from being GC'd.
        // MetadataRef and NamespaceTypeIndex caches are fingerprint-keyed (path + timestamp +
        // assemblySetHash) so stale entries expire naturally via LRU on the next rebuild.
        var normalized = Path.GetFullPath(assemblyPath);
        var prefix = normalized + "|";
        foreach (var key in _evalRunnerCache.Keys
                     .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                     .ToList())
        {
            _evalRunnerCache.TryRemove(key, out _);
        }
    }

    private static string ComputeRequestHash(TranslationRequest request)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(request.Expression).Append('\0');
        sb.Append(request.ContextVariableName).Append('\0');
        foreach (var imp in request.AdditionalImports)
            sb.Append(imp).Append('\0');
        foreach (var kv in request.UsingAliases.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        foreach (var s in request.UsingStaticTypes)
            sb.Append(s).Append('\0');
        foreach (var kv in request.LocalVariableTypes.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\0');
        sb.Append("useAsyncRunner=").Append(request.UseAsyncRunner ? '1' : '0').Append('\0');
        sb.Append("payloadContractVersion=").Append(QueryLensGeneratedPayloadContract.Version).Append('\0');

        if (request.DbContextResolution != null)
        {
            sb.Append("dbContextResolution=")
              .Append(request.DbContextResolution.DeclaredTypeName ?? string.Empty).Append('|')
              .Append(request.DbContextResolution.FactoryTypeName ?? string.Empty).Append('|')
              .Append(request.DbContextResolution.ResolutionSource ?? string.Empty).Append('|')
              .Append(request.DbContextResolution.Confidence.ToString(CultureInfo.InvariantCulture))
              .Append('|');
            foreach (var candidate in request.DbContextResolution.FactoryCandidateTypeNames.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(candidate).Append(';');
            sb.Append('\0');
        }
        
        // Include UsingContextSnapshot to ensure same expression with different using contexts
        // results in separate cache entries (reduces cache collisions)
        if (request.UsingContextSnapshot != null)
        {
            sb.Append("usingSnapshot={");
            foreach (var imp in request.UsingContextSnapshot.Imports.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(imp).Append(';');
            sb.Append('|');
            foreach (var kv in request.UsingContextSnapshot.Aliases.OrderBy(x => x.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            sb.Append('|');
            foreach (var st in request.UsingContextSnapshot.StaticTypes.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(st).Append(';');
            sb.Append("}\\0");
        }
        
        // Include ExpressionMetadata source location for validation
        if (request.ExpressionMetadata != null)
        {
            sb.Append("exprMeta=").Append(request.ExpressionMetadata.SourceLine).Append(':')
              .Append(request.ExpressionMetadata.SourceCharacter).Append('\0');
        }
        
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private void LogDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-Eval] {message}");
    }

    private bool ShouldDumpGeneratedSource() => _dumpSourceEnabled;

    private string DumpGeneratedSourceToTemp(string source)
    {
        try
        {
            var tempDir = Path.GetTempPath();
            Directory.CreateDirectory(tempDir);

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
                var suffix = attempt == 0 ? string.Empty : $"_{attempt}";
                var path = Path.Combine(tempDir, $"ql_eval_{timestamp}{suffix}.cs");

                try
                {
                    File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    return path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Rare same-millisecond filename collision; retry with suffix.
                }
            }

            return "(could not write temp file)";
        }
        catch
        {
            return "(could not write temp file)";
        }
    }

    private (HashSet<string> Namespaces, HashSet<string> Types) GetOrBuildNamespaceTypeIndex(
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

        // Namespace/type index should be stable for a given assembly context snapshot.
        // Use the context identity as fingerprint to avoid churn from incidental
        // assembly-load differences across requests in the same daemon session.
        var currentFingerprint = namespaceIndexCacheKey;

        var lookupWatch = Stopwatch.StartNew();
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

        // Keep namespace/type index aligned with the same filtered assembly set used by metadata refs.
        var buildWatch = Stopwatch.StartNew();
        var result = BuildKnownNamespaceAndTypeIndex(filteredAssemblies);
        buildWatch.Stop();
        _namespaceTypeIndexCache.Set(
            namespaceIndexCacheKey,
            currentFingerprint,
            result.Namespaces,
            result.Types);
        var metricsAfterBuild = _namespaceTypeIndexCache.GetMetrics();
        Console.Error.WriteLine(
            $"[QL-Engine] namespace-index cache=miss lookupMs={lookupWatch.Elapsed.TotalMilliseconds:0.###} " +
            $"buildMs={buildWatch.Elapsed.TotalMilliseconds:0.###} hits={metricsAfterBuild.Hits} " +
            $"misses={metricsAfterBuild.Misses} count={metricsAfterBuild.Count}");
        return result;
    }

    private static string ComputeAssemblyFingerprint(IReadOnlyList<Assembly> assemblies)
    {
        var sb = new StringBuilder();
        foreach (var asm in assemblies
                     .OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            var name = asm.GetName().Name ?? string.Empty;
            string mvid;
            try
            {
                mvid = asm.ManifestModule.ModuleVersionId.ToString("N");
            }
            catch
            {
                mvid = asm.FullName ?? string.Empty;
            }

            sb.Append(name).Append('|').Append(mvid).Append(';');
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes)[..16];
    }

    private static long GetUtcNowTicks() => DateTime.UtcNow.Ticks;

    private static void TrimCacheByLastAccess<TKey, TValue>(
        ConcurrentDictionary<TKey, TValue> cache,
        int maxEntries,
        Func<TValue, long> getLastAccess)
        where TKey : notnull
    {
        var overflow = cache.Count - maxEntries;
        if (overflow <= 0)
            return;

        foreach (var key in cache
                     .OrderBy(kvp => getLastAccess(kvp.Value))
                     .Take(overflow)
                     .Select(kvp => kvp.Key)
                     .ToList())
        {
            cache.TryRemove(key, out _);
        }
    }

    private void TouchMetadataRefCacheEntry(string key, MetadataRefEntry entry)
    {
        _refCache.TryUpdate(
            key,
            entry with { LastAccessTicks = GetUtcNowTicks() },
            entry);
    }

    private void TouchEvalRunnerCacheEntry(string key, EvalRunnerEntry entry)
    {
        _evalRunnerCache.TryUpdate(
            key,
            entry with { LastAccessTicks = GetUtcNowTicks() },
            entry);
    }

    // Roslyn compilation options are reused across all eval compilations.
    private static readonly CSharpCompilationOptions SCompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            allowUnsafe: false,
            nullableContextOptions: NullableContextOptions.Annotations);

    private static readonly CSharpParseOptions SParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    /// <summary>
    /// Translates a LINQ expression to SQL via execution-based SQL capture.
    /// </summary>
    public Task<QueryTranslationResult> EvaluateAsync(
        ProjectAssemblyContext alcCtx,
        TranslationRequest request,
        CancellationToken ct = default)
        => EvaluateAsyncInternal(alcCtx, request, ct, null, null);

    internal Task<QueryTranslationResult> EvaluateAsync(
        ProjectAssemblyContext alcCtx,
        TranslationRequest request,
        CancellationToken ct,
        IDbContextPoolProvider? dbContextPoolProvider,
        string? poolAssemblyPath)
        => EvaluateAsyncInternal(alcCtx, request, ct, dbContextPoolProvider, poolAssemblyPath);
}
