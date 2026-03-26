using EFQueryLens.Core.Common;

namespace EFQueryLens.Core.AssemblyContext;

internal sealed partial class ShadowAssemblyCache
{
    public void RunStartupCleanup()
    {
        try { CleanupCore(); }
        catch { /* best-effort */ }
    }

    private void CleanupCore()
    {
        try
        {
            if (!Directory.Exists(_root)) return;

            var maxAgeHours = EnvironmentVariableParser.ReadInt(
                "QUERYLENS_SHADOW_CACHE_MAX_AGE_HOURS",
                DefaultShadowCacheMaxAgeHours, min: 1, max: 720);
            var maxBundles = EnvironmentVariableParser.ReadInt(
                "QUERYLENS_SHADOW_CACHE_MAX_BUNDLES",
                DefaultShadowCacheMaxBundles, min: 1, max: 500);
            var cutoff = DateTime.UtcNow.AddHours(-maxAgeHours);

            // Delete stale staging dirs
            if (Directory.Exists(_stagingRoot))
            {
                foreach (var stagingDir in Directory.EnumerateDirectories(_stagingRoot))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(stagingDir) < cutoff)
                            TryDeleteDirectory(stagingDir);
                    }
                    catch { /* ignore */ }
                }
            }

            // Delete old bundles by age
            if (!Directory.Exists(_bundleRoot)) return;
            var bundleDirs = Directory.EnumerateDirectories(_bundleRoot)
                .Select(p => new DirectoryInfo(p))
                .OrderBy(d => d.LastWriteTimeUtc)
                .ToList();

            foreach (var dir in bundleDirs.ToList())
            {
                if (dir.LastWriteTimeUtc < cutoff)
                {
                    TryDeleteDirectory(dir.FullName);
                    bundleDirs.Remove(dir);
                }
            }

            // Trim to max bundle count (oldest first)
            while (bundleDirs.Count > maxBundles)
            {
                TryDeleteDirectory(bundleDirs[0].FullName);
                bundleDirs.RemoveAt(0);
            }

            if (_debugEnabled)
                Console.Error.WriteLine($"[QL-Engine] shadow-cache-cleanup root={_root}");
        }
        catch { /* best-effort */ }
    }

    private static void TouchDirectory(string path)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // Ignore best-effort touch failures.
        }
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Ignore locked or transient IO failures.
        }
    }
}
