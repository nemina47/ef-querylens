using Microsoft.CodeAnalysis.Scripting;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace QueryLens.Core.Scripting;

/// <summary>
/// Thread-safe cache of warm Roslyn <see cref="ScriptState{T}"/> instances,
/// keyed by assembly path + DbContext type name + a hash of the loaded assembly
/// set used to compile the state.
///
/// A warm state amortizes the cost of Roslyn compilation (~500ms cold start)
/// across subsequent calls. Each entry is invalidated when the assembly's
/// last-write timestamp changes (i.e., a rebuild happened) or when the set of
/// loaded assemblies changes (i.e., a new transitive dependency was discovered).
/// </summary>
internal sealed class ScriptStateCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();

    /// <summary>
    /// Returns a cached entry if one exists and is still fresh; otherwise null.
    /// Callers that get null must build a new state and call <see cref="Store"/>.
    /// </summary>
    public ScriptState<object>? TryGet(
        string assemblyPath,
        string dbContextTypeName,
        DateTime assemblyTimestamp,
        string assemblySetHash,
        string scriptContextHash)
    {
        var key = MakeKey(assemblyPath, dbContextTypeName);
        if (_entries.TryGetValue(key, out var entry)
            && entry.AssemblyTimestamp == assemblyTimestamp
            && entry.AssemblySetHash  == assemblySetHash
            && entry.ScriptContextHash == scriptContextHash)
            return entry.State;

        // Stale or compiled against a different assembly set — evict.
        _entries.TryRemove(key, out _);
        return null;
    }

    /// <summary>
    /// Stores a freshly-built <see cref="ScriptState{T}"/> in the cache.
    /// </summary>
    public void Store(
        string assemblyPath,
        string dbContextTypeName,
        DateTime assemblyTimestamp,
        string assemblySetHash,
        string scriptContextHash,
        ScriptState<object> state)
    {
        var key = MakeKey(assemblyPath, dbContextTypeName);
        _entries[key] = new CacheEntry(state, assemblyTimestamp, assemblySetHash, scriptContextHash);
    }

    /// <summary>Removes all cached entries for the given assembly path.</summary>
    public void Invalidate(string assemblyPath)
    {
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(assemblyPath + "::", StringComparison.Ordinal)))
            _entries.TryRemove(key, out _);
    }

    /// <summary>
    /// Computes a short, stable hash over the sorted set of assembly file paths
    /// currently loaded in <paramref name="alcCtx"/>. Used as part of the cache key
    /// to ensure a <see cref="ScriptState{T}"/> compiled against one set of
    /// metadata references is never served when the loaded set has grown (e.g. after
    /// a lazy transitive dependency is discovered).
    /// </summary>
    public static string ComputeAssemblySetHash(System.Reflection.Assembly[] loadedAssemblies)
    {
        // Sort for determinism — assembly enumeration order is not guaranteed.
        var paths = loadedAssemblies
            .Select(a => a.Location)
            .Where(l => !string.IsNullOrEmpty(l))
            .Order(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        foreach (var p in paths)
            sb.Append(p).Append('|');

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));

        // 8 hex chars (32 bits) is more than sufficient to distinguish
        // "Projectables loaded" vs "Projectables not yet loaded".
        return Convert.ToHexString(bytes)[..8];
    }

    private static string MakeKey(string assemblyPath, string dbContextTypeName) =>
        $"{assemblyPath}::{dbContextTypeName}";

    private sealed record CacheEntry(
        ScriptState<object> State,
        DateTime AssemblyTimestamp,
        string AssemblySetHash,
        string ScriptContextHash);
}
