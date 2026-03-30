using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;
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

    // Compiled + loaded eval runner cache: skip the entire Roslyn pipeline on warm hits.
    // Keys follow the pattern: "shadowAssemblyPath|timestampTicks|assemblySetHash|dbContextTypeName|requestHash"
    // Evicted whenever the ALC for a shadow assembly is released (InvalidateMetadataRefCache).
    private sealed record EvalRunnerEntry(Assembly EvalAssembly, MethodInfo RunMethod, long LastAccessTicks, string? ExecutedExpression = null);
    private readonly ConcurrentDictionary<string, EvalRunnerEntry> _evalRunnerCache = new(StringComparer.Ordinal);

    // Known namespace/type index cache keyed by assemblySetHash — the scan is expensive
    // on large projects but the result only changes when the assembly set changes.
    // The assemblySetHash is computed from shadow bundle paths, which change on every
    // rebuild, so stale entries expire naturally via LRU without explicit eviction.
    private sealed record NamespaceTypeIndexEntry(
        HashSet<string> Namespaces,
        HashSet<string> Types,
        long LastAccessTicks);

    private readonly ConcurrentDictionary<string, NamespaceTypeIndexEntry>
        _namespaceTypeIndexCache = new(StringComparer.Ordinal);

    private readonly bool _debugEnabled = 
        EFQueryLens.Core.Common.EnvironmentVariableParser.ReadBool("QUERYLENS_DEBUG", fallback: false);

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

    private (HashSet<string> Namespaces, HashSet<string> Types) GetOrBuildNamespaceTypeIndex(
        string assemblySetHash,
        IReadOnlyList<Assembly> compilationAssemblies)
    {
        if (_namespaceTypeIndexCache.TryGetValue(assemblySetHash, out var cached))
        {
            TouchNamespaceTypeIndexCacheEntry(assemblySetHash, cached);
            return (cached.Namespaces, cached.Types);
        }

        // Filter the same assemblies excluded from metadata refs so knownTypes/knownNamespaces
        // stay consistent with what the Roslyn compiler actually sees. Without this, Roslyn
        // assemblies (loaded by the LSP process) appear in knownNamespaces but not in metadata
        // refs, causing the compile-retry loop to synthesize 'using Microsoft.CodeAnalysis.*'
        // directives that then fail with CS0234.
        var filteredAssemblies = compilationAssemblies
            .Where(a =>
            {
                var loc = a.Location;
                if (string.IsNullOrEmpty(loc)) return false;
                var normalizedLoc = loc.Replace('\\', '/');
                if (normalizedLoc.Contains("/runtimes/", StringComparison.OrdinalIgnoreCase)) return false;
                var name = a.GetName().Name;
                return !ShouldSkipMetadataReferenceAssembly(name);
            })
            .ToList();
        var result = BuildKnownNamespaceAndTypeIndex(filteredAssemblies);
        _namespaceTypeIndexCache[assemblySetHash] = new NamespaceTypeIndexEntry(
            result.Namespaces,
            result.Types,
            GetUtcNowTicks());
        TrimCacheByLastAccess(_namespaceTypeIndexCache, MaxNamespaceTypeIndexCacheEntries, static e => e.LastAccessTicks);
        return result;
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

    private void TouchNamespaceTypeIndexCacheEntry(string key, NamespaceTypeIndexEntry entry)
    {
        _namespaceTypeIndexCache.TryUpdate(
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
