using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting;
using EFQueryLens.Core.Scripting.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace EFQueryLens.Daemon;

internal sealed class DaemonRuntime(IMemoryCache cache)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Lazy<Task<QueryTranslationResult>>> _inflight =
        new(StringComparer.Ordinal);

    private DateTime _lastActivity = DateTime.UtcNow;
    private long _cacheHits;
    private long _cacheMisses;

    internal void Touch() => _lastActivity = DateTime.UtcNow;

    internal DateTime LastActivity => _lastActivity;

    internal (long Hits, long Misses) ReadStats() =>
        (Interlocked.Read(ref _cacheHits), Interlocked.Read(ref _cacheMisses));

    internal bool TryGetCached(string cacheKey, out QueryTranslationResult? result)
    {
        if (cache.TryGetValue<QueryTranslationResult>(cacheKey, out result) && result is not null)
        {
            Interlocked.Increment(ref _cacheHits);
            return true;
        }

        Interlocked.Increment(ref _cacheMisses);
        return false;
    }

    internal void SetCached(string cacheKey, QueryTranslationResult result)
    {
        cache.Set(cacheKey, result, CacheTtl);
    }

    internal bool IsCached(string cacheKey) =>
        cache.TryGetValue<QueryTranslationResult>(cacheKey, out _);

    internal Lazy<Task<QueryTranslationResult>> GetOrAddInflight(
        string cacheKey,
        Func<string, Lazy<Task<QueryTranslationResult>>> factory) =>
        _inflight.GetOrAdd(cacheKey, factory);

    internal void RemoveInflight(string cacheKey) =>
        _inflight.TryRemove(cacheKey, out _);

    internal void ClearCache()
    {
        if (cache is MemoryCache mc)
            mc.Clear();
    }

    /// <summary>
    /// Returns the first 16 hex characters of the SHA256 of all <see cref="TranslationRequest"/>
    /// fields that affect the compiled eval assembly or its stub declarations.
    /// </summary>
    internal static string ComputeCacheKey(TranslationRequest r)
    {
        var sb = new StringBuilder();
        sb.Append(r.Expression).Append('\0');
        sb.Append(r.AssemblyPath ?? string.Empty).Append('\0');
        sb.Append(r.DbContextTypeName ?? string.Empty).Append('\0');
        sb.Append(r.ContextVariableName).Append('\0');
        sb.Append("requestContractVersion=").Append(r.RequestContractVersion).Append('\0');
        sb.Append("useAsyncRunner=").Append(r.UseAsyncRunner ? '1' : '0').Append('\0');
        sb.Append("payloadContractVersion=").Append(QueryLensGeneratedPayloadContract.Version).Append('\0');
        foreach (var ns in r.AdditionalImports.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(ns).Append('\0');
        foreach (var kv in r.UsingAliases.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\0');
        foreach (var st in r.UsingStaticTypes.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(st).Append('\0');
        foreach (var hint in r.LocalSymbolGraph
                     .OrderBy(h => h.DeclarationOrder)
                     .ThenBy(h => h.Name, StringComparer.Ordinal))
        {
            sb.Append("sym:")
                .Append(hint.DeclarationOrder).Append(':')
                .Append(hint.Name).Append(':')
                .Append(hint.TypeName).Append(':')
                .Append(hint.Kind).Append(':')
                .Append(hint.Scope ?? string.Empty).Append(':')
                .Append(hint.ReplayPolicy).Append(':')
                .Append(hint.InitializerExpression ?? string.Empty)
                .Append('\0');
            foreach (var dep in hint.Dependencies.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append("dep:").Append(hint.Name).Append("->").Append(dep).Append('\0');
        }

        if (r.V2CapturePlan is not null)
        {
            sb.Append("v2Capture:")
                .Append(r.V2CapturePlan.IsComplete ? '1' : '0')
                .Append(':')
                .Append(r.V2CapturePlan.ExecutableExpression)
                .Append('\0');

            foreach (var entry in r.V2CapturePlan.Entries
                         .OrderBy(x => x.DeclarationOrder)
                         .ThenBy(x => x.Name, StringComparer.Ordinal))
            {
                sb.Append("v2sym:")
                    .Append(entry.DeclarationOrder).Append(':')
                    .Append(entry.Name).Append(':')
                    .Append(entry.TypeName).Append(':')
                    .Append(entry.Kind).Append(':')
                    .Append(entry.Scope ?? string.Empty).Append(':')
                    .Append(entry.CapturePolicy).Append(':')
                    .Append(entry.InitializerExpression ?? string.Empty).Append(':')
                    .Append(entry.RejectCode ?? string.Empty)
                    .Append('\0');
                foreach (var dep in entry.Dependencies.OrderBy(x => x, StringComparer.Ordinal))
                    sb.Append("v2dep:").Append(entry.Name).Append("->").Append(dep).Append('\0');
            }

            foreach (var diagnostic in r.V2CapturePlan.Diagnostics
                         .OrderBy(x => x.Code, StringComparer.Ordinal)
                         .ThenBy(x => x.SymbolName, StringComparer.Ordinal))
            {
                sb.Append("v2diag:")
                    .Append(diagnostic.Code).Append(':')
                    .Append(diagnostic.SymbolName).Append(':')
                    .Append(diagnostic.Message)
                    .Append('\0');
            }
        }

        if (r.V2ExtractionPlan is not null)
        {
            sb.Append("v2Extract:")
                .Append(r.V2ExtractionPlan.BoundaryKind).Append(':')
                .Append(r.V2ExtractionPlan.NeedsMaterialization ? '1' : '0').Append(':')
                .Append(r.V2ExtractionPlan.RootContextVariableName).Append(':')
                .Append(r.V2ExtractionPlan.RootMemberName)
                .Append('\0');

            foreach (var helper in r.V2ExtractionPlan.AppliedHelperMethods.OrderBy(x => x, StringComparer.Ordinal))
            {
                sb.Append("v2helper:").Append(helper).Append('\0');
            }

            foreach (var diagnostic in r.V2ExtractionPlan.Diagnostics
                         .OrderBy(x => x.Code, StringComparer.Ordinal))
            {
                sb.Append("v2ExtDiag:").Append(diagnostic.Code).Append(':').Append(diagnostic.Message).Append('\0');
            }
        }

        if (r.DbContextResolution is not null)
        {
            sb.Append("dbContextResolution=")
              .Append(r.DbContextResolution.DeclaredTypeName ?? string.Empty).Append('|')
              .Append(r.DbContextResolution.FactoryTypeName ?? string.Empty).Append('|')
              .Append(r.DbContextResolution.ResolutionSource ?? string.Empty).Append('|')
              .Append(r.DbContextResolution.Confidence.ToString(CultureInfo.InvariantCulture))
              .Append('|');
            foreach (var candidate in r.DbContextResolution.FactoryCandidateTypeNames.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(candidate).Append(';');
            sb.Append('\0');
        }

        if (r.UsingContextSnapshot is not null)
        {
            sb.Append("usingSnapshot={");
            foreach (var imp in r.UsingContextSnapshot.Imports.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(imp).Append(';');
            sb.Append('|');
            foreach (var kv in r.UsingContextSnapshot.Aliases.OrderBy(x => x.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            sb.Append('|');
            foreach (var st in r.UsingContextSnapshot.StaticTypes.OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(st).Append(';');
            sb.Append("}\\0");
        }

        if (r.ExpressionMetadata is not null)
        {
            sb.Append("exprMeta=")
              .Append(r.ExpressionMetadata.ExpressionType ?? string.Empty).Append('|')
              .Append(r.ExpressionMetadata.SourceLine).Append(':')
              .Append(r.ExpressionMetadata.SourceCharacter).Append('|')
              .Append(r.ExpressionMetadata.Confidence.ToString(CultureInfo.InvariantCulture))
              .Append('\0');
        }

        if (r.ExtractionOrigin is not null)
        {
            sb.Append("origin=")
              .Append(r.ExtractionOrigin.FilePath ?? string.Empty).Append('|')
              .Append(r.ExtractionOrigin.Line).Append(':')
              .Append(r.ExtractionOrigin.Character).Append('|')
              .Append(r.ExtractionOrigin.EndLine).Append(':')
              .Append(r.ExtractionOrigin.EndCharacter).Append('|')
              .Append(r.ExtractionOrigin.Scope ?? string.Empty)
              .Append('\0');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    internal static void ValidateSnapshotConsistency(TranslationRequest request)
    {
        if (request.RequestContractVersion != TranslationRequestContract.Version)
        {
            throw new InvalidOperationException(
                $"Unsupported translation request contract version {request.RequestContractVersion}. Expected {TranslationRequestContract.Version}.");
        }

        if (request.ExtractionOrigin is null
            || string.IsNullOrWhiteSpace(request.ExtractionOrigin.FilePath)
            || request.ExtractionOrigin.Line < 0
            || request.ExtractionOrigin.Character < 0
            || request.ExtractionOrigin.EndLine < 0
            || request.ExtractionOrigin.EndCharacter < 0)
        {
            throw new InvalidOperationException("Missing or invalid extraction origin in translation request.");
        }

        // Validate v2 extraction plan if present (slice 3 support)
        if (request.V2ExtractionPlan is not null)
        {
            if (string.IsNullOrWhiteSpace(request.V2ExtractionPlan.Expression))
                throw new InvalidOperationException("V2 extraction plan has empty expression.");
            
            if (string.IsNullOrWhiteSpace(request.V2ExtractionPlan.ContextVariableName))
                throw new InvalidOperationException("V2 extraction plan missing context variable name.");
                
            if (string.IsNullOrWhiteSpace(request.V2ExtractionPlan.RootContextVariableName))
                throw new InvalidOperationException("V2 extraction plan missing root context variable name.");
                
            if (request.V2ExtractionPlan.BoundaryKind != "Materialized" && request.V2ExtractionPlan.BoundaryKind != "Queryable")
                throw new InvalidOperationException($"V2 extraction plan invalid boundary kind: {request.V2ExtractionPlan.BoundaryKind}.");
        }

        if (request.UsingContextSnapshot is null)
            return;

        var importsMatch = request.AdditionalImports
            .OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(
                request.UsingContextSnapshot.Imports.OrderBy(x => x, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var staticTypesMatch = request.UsingStaticTypes
            .OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(
                request.UsingContextSnapshot.StaticTypes.OrderBy(x => x, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var aliasesMatch = request.UsingAliases.Count == request.UsingContextSnapshot.Aliases.Count
            && request.UsingAliases.OrderBy(x => x.Key, StringComparer.Ordinal)
                .SequenceEqual(request.UsingContextSnapshot.Aliases.OrderBy(x => x.Key, StringComparer.Ordinal));

        if (!importsMatch || !staticTypesMatch || !aliasesMatch)
        {
            Console.Error.WriteLine(
                "[QL-Engine] request-snapshot-mismatch additionalImports/usingAliases/usingStaticTypes do not match UsingContextSnapshot");
        }
    }
}
