namespace EFQueryLens.Lsp.Parsing;

public static partial class AssemblyResolver
{
    private static bool TryGetCachedTargetAssembly(string sourceFilePath, out string targetAssemblyPath)
    {
        targetAssemblyPath = string.Empty;

        if (TargetAssemblyCacheTtlMs <= 0)
        {
            return false;
        }

        if (!TargetAssemblyCache.TryGetValue(sourceFilePath, out var cached))
        {
            return false;
        }

        if (cached.ExpiresAtUtcTicks <= DateTime.UtcNow.Ticks || !File.Exists(cached.TargetAssemblyPath))
        {
            TargetAssemblyCache.TryRemove(sourceFilePath, out _);
            return false;
        }

        targetAssemblyPath = cached.TargetAssemblyPath;
        return true;
    }

    private static void CacheTargetAssembly(string sourceFilePath, string? resolvedAssemblyPath)
    {
        if (TargetAssemblyCacheTtlMs <= 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(resolvedAssemblyPath)
            || resolvedAssemblyPath.StartsWith("DEBUG_FAIL", StringComparison.Ordinal)
            || !File.Exists(resolvedAssemblyPath))
        {
            return;
        }

        var expires = DateTime.UtcNow.AddMilliseconds(TargetAssemblyCacheTtlMs).Ticks;
        TargetAssemblyCache[sourceFilePath] = new CachedAssemblySelection(resolvedAssemblyPath, expires);
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
}
