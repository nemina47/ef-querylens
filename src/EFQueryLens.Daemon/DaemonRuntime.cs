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
        sb.Append("useAsyncRunner=").Append(r.UseAsyncRunner ? '1' : '0').Append('\0');
        sb.Append("payloadContractVersion=").Append(QueryLensGeneratedPayloadContract.Version).Append('\0');
        foreach (var ns in r.AdditionalImports.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(ns).Append('\0');
        foreach (var kv in r.UsingAliases.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\0');
        foreach (var st in r.UsingStaticTypes.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(st).Append('\0');
        foreach (var kv in r.LocalVariableTypes.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\0');

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

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    internal static void ValidateSnapshotConsistency(TranslationRequest request)
    {
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
