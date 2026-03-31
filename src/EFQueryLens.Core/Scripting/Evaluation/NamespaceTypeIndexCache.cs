using System.Collections.Concurrent;

namespace EFQueryLens.Core.Scripting.Evaluation;

public interface INamespaceTypeIndexCache
{
    bool TryGet(
        string assemblySetHash,
        string assemblyFingerprint,
        out HashSet<string> namespaces,
        out HashSet<string> types);

    void Set(
        string assemblySetHash,
        string assemblyFingerprint,
        HashSet<string> namespaces,
        HashSet<string> types);

    (long Hits, long Misses, int Count) GetMetrics();
}

public sealed class NamespaceTypeIndexCache(int maxEntries) : INamespaceTypeIndexCache
{
    private sealed record Entry(
        HashSet<string> Namespaces,
        HashSet<string> Types,
        string AssemblyFingerprint,
        long LastAccessTicks);

    private readonly int _maxEntries = Math.Max(1, maxEntries);
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private long _hits;
    private long _misses;

    public bool TryGet(
        string assemblySetHash,
        string assemblyFingerprint,
        out HashSet<string> namespaces,
        out HashSet<string> types)
    {
        if (_cache.TryGetValue(assemblySetHash, out var entry)
            && string.Equals(entry.AssemblyFingerprint, assemblyFingerprint, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _hits);
            _cache.TryUpdate(
                assemblySetHash,
                entry with { LastAccessTicks = DateTime.UtcNow.Ticks },
                entry);
            namespaces = entry.Namespaces;
            types = entry.Types;
            return true;
        }

        Interlocked.Increment(ref _misses);
        namespaces = [];
        types = [];
        return false;
    }

    public void Set(
        string assemblySetHash,
        string assemblyFingerprint,
        HashSet<string> namespaces,
        HashSet<string> types)
    {
        _cache[assemblySetHash] = new Entry(namespaces, types, assemblyFingerprint, DateTime.UtcNow.Ticks);
        TrimIfNeeded();
    }

    public (long Hits, long Misses, int Count) GetMetrics()
        => (Interlocked.Read(ref _hits), Interlocked.Read(ref _misses), _cache.Count);

    private void TrimIfNeeded()
    {
        var overflow = _cache.Count - _maxEntries;
        if (overflow <= 0)
            return;

        foreach (var key in _cache
                     .OrderBy(kvp => kvp.Value.LastAccessTicks)
                     .Take(overflow)
                     .Select(kvp => kvp.Key)
                     .ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }
}
