namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class WarmupHandler
{
    private bool TryGetCachedWarmup(string assemblyPath, out CachedWarmup cached)
    {
        cached = default!;
        if (!_warmCache.TryGetValue(assemblyPath, out var existing))
        {
            return false;
        }

        if (existing.ExpiresAtUtcTicks <= DateTime.UtcNow.Ticks)
        {
            _warmCache.TryRemove(assemblyPath, out _);
            return false;
        }

        cached = existing;
        return true;
    }

    private void CacheWarmup(string assemblyPath, bool success, string message)
    {
        var ttlMs = success ? _successTtlMs : _failureCooldownMs;
        if (ttlMs <= 0)
        {
            _warmCache.TryRemove(assemblyPath, out _);
            return;
        }

        var expires = DateTime.UtcNow.AddMilliseconds(ttlMs).Ticks;
        _warmCache[assemblyPath] = new CachedWarmup(expires, success, message);
    }

    private static int ReadIntEnvironmentVariable(string variableName, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (!int.TryParse(raw, out var value))
        {
            return fallback;
        }

        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

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

        Console.Error.WriteLine($"[QL-Warmup] {message}");
    }
}
