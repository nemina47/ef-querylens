namespace EFQueryLens.Core.AssemblyContext;

internal sealed partial class ShadowAssemblyCache
{
    private const int DefaultShadowCacheMaxAgeHours = 48;
    private const int DefaultShadowCacheMaxBundles = 20;

    private readonly bool _debugEnabled;
    private readonly string _root;
    private readonly string _bundleRoot;
    private readonly string _stagingRoot;
    private readonly Lock _gate = new();

    public ShadowAssemblyCache(bool debugEnabled)
    {
        _debugEnabled = debugEnabled;
        _root = ResolveShadowRoot();
        _bundleRoot = Path.Combine(_root, "bundles");
        _stagingRoot = Path.Combine(_root, "staging");

        Directory.CreateDirectory(_bundleRoot);
        Directory.CreateDirectory(_stagingRoot);
    }

    private static string ResolveShadowRoot()
    {
        var envOverride = Environment.GetEnvironmentVariable("QUERYLENS_SHADOW_ROOT");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return envOverride.Trim();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EFQueryLens",
            "shadow");
    }

    public string ResolveOrCreateBundle(string sourceAssemblyPath)
    {
        var fullSourcePath = Path.GetFullPath(sourceAssemblyPath);
        var sourceDir = Path.GetDirectoryName(fullSourcePath)
            ?? throw new InvalidOperationException($"Could not determine source directory for '{fullSourcePath}'.");

        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source output directory not found: {sourceDir}");
        }

        var manifest = BuildManifest(sourceDir);
        var bundleKey = ComputeBundleKey(sourceDir, manifest);
        var bundlePath = Path.Combine(_bundleRoot, bundleKey);
        var bundleAssemblyPath = Path.Combine(bundlePath, Path.GetFileName(fullSourcePath));

        if (File.Exists(bundleAssemblyPath))
        {
            lock (_gate)
            {
                TouchDirectory(bundlePath);
            }

            return bundleAssemblyPath;
        }

        var stagingPath = Path.Combine(_stagingRoot, $"{bundleKey}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);

        try
        {
            foreach (var entry in manifest)
            {
                var targetPath = Path.Combine(stagingPath, entry.RelativePath);
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(entry.FullPath, targetPath, overwrite: true);
            }

            lock (_gate)
            {
                if (!File.Exists(bundleAssemblyPath))
                {
                    TryAtomicPromote(stagingPath, bundlePath);
                    Task.Run(() => { try { CleanupCore(); } catch { } });
                }

                TouchDirectory(bundlePath);
            }
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }

        return bundleAssemblyPath;
    }
}
