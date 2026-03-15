using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using EFQueryLens.Core.AssemblyContext;

namespace EFQueryLens.Core.Scripting;

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
/// SQL is captured by installing an <see cref="OfflineDbConnection"/> on the DbContext
/// before execution. The connection's command stubs intercept every
/// <c>DbCommand.Execute*</c> call, record the SQL + parameters into
/// <see cref="SqlCaptureScope"/> (an <c>AsyncLocal</c>-based collector), and return a
/// <see cref="FakeDbDataReader"/> so EF Core materializes "rows" without error.
/// </para>
///
/// No real database connection is ever opened.
/// </summary>
public sealed partial class QueryEvaluator
{
    // Building MetadataReference objects from disk is expensive (100-500 ms for a
    // large project). Cache them keyed on assembly path + last-write timestamp +
    // assembly-set hash so the cost is paid only on initial load or after a rebuild.
    private sealed record MetadataRefEntry(
        DateTime AssemblyTimestamp,
        string AssemblySetHash,
        MetadataReference[] Refs);

    private readonly ConcurrentDictionary<string, MetadataRefEntry> _refCache = new();

    // Compiled + loaded eval runner cache: skip the entire Roslyn pipeline on warm hits.
    // Keys follow the pattern: "shadowAssemblyPath|timestampTicks|assemblySetHash|dbContextTypeName|requestHash"
    // Evicted whenever the ALC for a shadow assembly is released (InvalidateMetadataRefCache).
    private sealed record EvalRunnerEntry(Assembly EvalAssembly, MethodInfo RunMethod);
    private readonly ConcurrentDictionary<string, EvalRunnerEntry> _evalRunnerCache = new(StringComparer.Ordinal);

    // Known namespace/type index cache keyed by assemblySetHash — the scan is expensive
    // on large projects but the result only changes when the assembly set changes.
    private readonly ConcurrentDictionary<string, (HashSet<string> Namespaces, HashSet<string> Types)>
        _namespaceTypeIndexCache = new(StringComparer.Ordinal);

    internal sealed record EvaluationStageTimings(
        TimeSpan? ContextResolution,
        TimeSpan? DbContextCreation,
        TimeSpan? MetadataReferenceBuild,
        TimeSpan? RoslynCompilation,
        int CompilationRetryCount,
        TimeSpan? EvalAssemblyLoad,
        TimeSpan? RunnerExecution,
        TimeSpan? ToQueryStringFallback);

    internal void InvalidateMetadataRefCache(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
            return;

        var normalized = Path.GetFullPath(assemblyPath);
        _refCache.TryRemove(normalized, out _);

        // Evict all eval runner cache entries for this assembly — they hold Assembly
        // references that would prevent the collectible ALC from being GC'd.
        var prefix = normalized + "|";
        foreach (var key in _evalRunnerCache.Keys
                     .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                     .ToList())
        {
            _evalRunnerCache.TryRemove(key, out _);
        }

        // The assembly set likely changed; discard the cached namespace/type index.
        _namespaceTypeIndexCache.Clear();
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
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private (HashSet<string> Namespaces, HashSet<string> Types) GetOrBuildNamespaceTypeIndex(
        string assemblySetHash,
        IReadOnlyList<Assembly> compilationAssemblies)
    {
        if (_namespaceTypeIndexCache.TryGetValue(assemblySetHash, out var cached))
            return cached;
        var result = BuildKnownNamespaceAndTypeIndex(compilationAssemblies);
        _namespaceTypeIndexCache[assemblySetHash] = result;
        return result;
    }

    // Roslyn compilation options are reused across all eval compilations.
    private static readonly CSharpCompilationOptions SCompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: false,
            nullableContextOptions: NullableContextOptions.Disable);

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
