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
    private const int MaxNamespaceTypeIndexCacheEntries = 64;
    internal delegate object? SyncRunnerInvoker(object dbInstance);
    internal delegate Task<object?> AsyncRunnerInvoker(object dbInstance, CancellationToken ct);

    private readonly EvalRunnerCache _evalRunnerCache = new();
    private readonly INamespaceTypeIndexCache _namespaceTypeIndexCache;
    private readonly CompilationPipeline _compilationPipeline;

    private readonly bool _debugEnabled = 
        EFQueryLens.Core.Common.EnvironmentVariableParser.ReadBool("QUERYLENS_DEBUG", fallback: false);

    private readonly bool _dumpSourceEnabled =
        EFQueryLens.Core.Common.EnvironmentVariableParser.ReadBool("QUERYLENS_DUMP_SOURCE", fallback: false);

    public QueryEvaluator(INamespaceTypeIndexCache? namespaceTypeIndexCache = null)
    {
        _namespaceTypeIndexCache = namespaceTypeIndexCache ?? new NamespaceTypeIndexCache(MaxNamespaceTypeIndexCacheEntries);
        _compilationPipeline = new CompilationPipeline(_namespaceTypeIndexCache, _debugEnabled, _dumpSourceEnabled);
    }

    internal sealed record EvaluationStageTimings(
        TimeSpan? ContextResolution,
        TimeSpan? DbContextCreation,
        TimeSpan? MetadataReferenceBuild,
        TimeSpan? RoslynCompilation,
        int CompilationRetryCount,
        TimeSpan? EvalAssemblyLoad,
        TimeSpan? RunnerExecution);

    internal void InvalidateMetadataRefCache(string assemblyPath) =>
        _evalRunnerCache.Invalidate(assemblyPath);

    private static string ComputeRequestHash(TranslationRequest request)
    {
        var sb = new System.Text.StringBuilder();
        AppendRequestShape(sb, request);
        AppendDbContextResolutionFingerprint(sb, request.DbContextResolution);
        AppendUsingContextSnapshotFingerprint(sb, request);
        AppendExpressionMetadataFingerprint(sb, request);
        
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static void AppendRequestShape(StringBuilder sb, TranslationRequest request)
    {
        sb.Append(request.Expression).Append('\0');
        sb.Append(request.OriginalExpression ?? string.Empty).Append('\0');
        sb.Append(request.RewrittenExpression ?? string.Empty).Append('\0');
        sb.Append(request.ContextVariableName).Append('\0');
        foreach (var import in request.AdditionalImports)
            sb.Append(import).Append('\0');
        foreach (var kv in request.UsingAliases.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        foreach (var staticType in request.UsingStaticTypes)
            sb.Append(staticType).Append('\0');
        sb.Append("requestContractVersion=").Append(request.RequestContractVersion).Append('\0');
        foreach (var symbol in request.LocalSymbolGraph
                     .OrderBy(s => s.DeclarationOrder)
                     .ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            sb.Append("sym:")
                .Append(symbol.DeclarationOrder).Append(':')
                .Append(symbol.Name).Append(':')
                .Append(symbol.TypeName).Append(':')
                .Append(symbol.Kind).Append(':')
                .Append(symbol.Scope ?? string.Empty).Append(':')
                .Append(symbol.ReplayPolicy).Append(':')
                .Append(symbol.InitializerExpression ?? string.Empty)
                .Append('\0');

            foreach (var dep in symbol.Dependencies.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append("dep:").Append(symbol.Name).Append("->").Append(dep).Append('\0');
        }
        sb.Append("useAsyncRunner=").Append(request.UseAsyncRunner ? '1' : '0').Append('\0');
        foreach (var flag in request.RewriteFlags.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append("rw:").Append(flag).Append('\0');
        sb.Append("payloadContractVersion=").Append(QueryLensGeneratedPayloadContract.Version).Append('\0');
    }

    private static void AppendDbContextResolutionFingerprint(StringBuilder sb, DbContextResolutionSnapshot? resolution)
    {
        if (resolution is null)
            return;

        sb.Append("dbContextResolution=")
          .Append(resolution.DeclaredTypeName ?? string.Empty).Append('|')
          .Append(resolution.FactoryTypeName ?? string.Empty).Append('|')
          .Append(resolution.ResolutionSource ?? string.Empty).Append('|')
          .Append(resolution.Confidence.ToString(CultureInfo.InvariantCulture))
          .Append('|');

        foreach (var candidate in resolution.FactoryCandidateTypeNames.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(candidate).Append(';');

        sb.Append('\0');
    }

    private static void AppendUsingContextSnapshotFingerprint(StringBuilder sb, TranslationRequest request)
    {
        // Include UsingContextSnapshot to ensure same expression with different using contexts
        // results in separate cache entries (reduces cache collisions).
        if (request.UsingContextSnapshot is null)
            return;

        var snapshot = request.UsingContextSnapshot;
        sb.Append("usingSnapshot={");
        foreach (var import in snapshot.Imports.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(import).Append(';');
        sb.Append('|');
        foreach (var kv in snapshot.Aliases.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        sb.Append('|');
        foreach (var staticType in snapshot.StaticTypes.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(staticType).Append(';');
        sb.Append("}\\0");
    }

    private static void AppendExpressionMetadataFingerprint(StringBuilder sb, TranslationRequest request)
    {
        if (request.ExpressionMetadata is null)
            return;

        var metadata = request.ExpressionMetadata;
        sb.Append("exprMeta=").Append(metadata.SourceLine).Append(':')
          .Append(metadata.SourceCharacter).Append('\0');

        if (request.ExtractionOrigin is null)
            return;

        sb.Append("origin=")
          .Append(request.ExtractionOrigin.FilePath ?? string.Empty).Append('|')
          .Append(request.ExtractionOrigin.Line).Append(':')
          .Append(request.ExtractionOrigin.Character).Append('|')
          .Append(request.ExtractionOrigin.EndLine).Append(':')
          .Append(request.ExtractionOrigin.EndCharacter).Append('|')
          .Append(request.ExtractionOrigin.Scope ?? string.Empty)
          .Append('\0');
    }

    private void LogDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-Eval] {message}");
    }

    internal static string DumpGeneratedSourceToTemp(string source)
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

    internal static long GetUtcNowTicks() => DateTime.UtcNow.Ticks;

    internal static void TrimCacheByLastAccess<TKey, TValue>(
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
