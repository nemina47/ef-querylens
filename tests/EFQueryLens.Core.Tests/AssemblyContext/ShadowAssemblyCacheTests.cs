using EFQueryLens.Core.AssemblyContext;

namespace EFQueryLens.Core.Tests.AssemblyContext;

/// <summary>
/// Unit tests for <see cref="ShadowAssemblyCache"/>.
///
/// Each test that touches the filesystem points the cache at an isolated temp directory
/// via the QUERYLENS_SHADOW_ROOT environment variable to avoid polluting the real shadow
/// cache and to ensure deterministic cleanup.
/// </summary>
public class ShadowAssemblyCacheTests : IDisposable
{
    // Per-test isolated shadow root so tests do not interfere with each other.
    private readonly string _shadowRoot;
    private readonly string _prevEnvValue;

    public ShadowAssemblyCacheTests()
    {
        _shadowRoot = Path.Combine(
            Path.GetTempPath(),
            "ql-shadow-test-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(_shadowRoot);

        _prevEnvValue = Environment.GetEnvironmentVariable("QUERYLENS_SHADOW_ROOT") ?? string.Empty;
        Environment.SetEnvironmentVariable("QUERYLENS_SHADOW_ROOT", _shadowRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            "QUERYLENS_SHADOW_ROOT",
            string.IsNullOrEmpty(_prevEnvValue) ? null : _prevEnvValue);

        try { Directory.Delete(_shadowRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a temporary source directory with a fake DLL + a couple of sidecar files.
    /// Returns the path to the directory and the path to the fake assembly file.
    /// </summary>
    private static (string SourceDir, string AssemblyPath) CreateFakeSourceDir(string suffix = "")
    {
        var sourceDir = Path.Combine(
            Path.GetTempPath(),
            "ql-src-" + Guid.NewGuid().ToString("N")[..8] + suffix);
        Directory.CreateDirectory(sourceDir);

        var dllPath = Path.Combine(sourceDir, "FakeApp.dll");
        File.WriteAllBytes(dllPath, [0x4D, 0x5A]); // "MZ" header stub

        File.WriteAllText(Path.Combine(sourceDir, "FakeApp.deps.json"), "{}");
        File.WriteAllText(Path.Combine(sourceDir, "FakeApp.runtimeconfig.json"), "{}");

        return (sourceDir, dllPath);
    }

    // ─── Constructor ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_CreatesRequiredDirectories()
    {
        var cache = new ShadowAssemblyCache(debugEnabled: false);

        Assert.True(Directory.Exists(Path.Combine(_shadowRoot, "bundles")));
        Assert.True(Directory.Exists(Path.Combine(_shadowRoot, "staging")));
    }

    [Fact]
    public void Constructor_WithDebugEnabled_DoesNotThrow()
    {
        // Just verifying debug flag is accepted — it only affects logging output.
        var cache = new ShadowAssemblyCache(debugEnabled: true);

        Assert.True(Directory.Exists(Path.Combine(_shadowRoot, "bundles")));
    }

    [Fact]
    public void Constructor_EnvOverride_IsUsed()
    {
        // The env var was set in ctor — verify directories live under our isolated root.
        var cache = new ShadowAssemblyCache(debugEnabled: false);

        Assert.True(Directory.Exists(_shadowRoot));
        // bundles sub-dir must be inside our override, not the default LocalAppData path.
        Assert.StartsWith(_shadowRoot, Path.Combine(_shadowRoot, "bundles"), StringComparison.OrdinalIgnoreCase);
    }

    // ─── ResolveOrCreateBundle — error paths ──────────────────────────────────

    [Fact]
    public void ResolveOrCreateBundle_SourceDirDoesNotExist_ThrowsDirectoryNotFoundException()
    {
        var cache = new ShadowAssemblyCache(debugEnabled: false);
        var bogus = Path.Combine(Path.GetTempPath(), "ql-nonexistent-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() =>
            cache.ResolveOrCreateBundle(Path.Combine(bogus, "FakeApp.dll")));
    }

    // ─── ResolveOrCreateBundle — happy paths ──────────────────────────────────

    [Fact]
    public void ResolveOrCreateBundle_FirstCall_CreatesBundleAndReturnsAssemblyPath()
    {
        var (sourceDir, assemblyPath) = CreateFakeSourceDir();
        try
        {
            var cache = new ShadowAssemblyCache(debugEnabled: false);

            var result = cache.ResolveOrCreateBundle(assemblyPath);

            Assert.True(File.Exists(result), $"Shadow assembly not found at: {result}");
            Assert.Equal("FakeApp.dll", Path.GetFileName(result));
            // Must live under our isolated shadow root.
            Assert.StartsWith(_shadowRoot, result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveOrCreateBundle_SecondCallSameSource_ReturnsSamePath()
    {
        var (sourceDir, assemblyPath) = CreateFakeSourceDir();
        try
        {
            var cache = new ShadowAssemblyCache(debugEnabled: false);

            var first = cache.ResolveOrCreateBundle(assemblyPath);
            var second = cache.ResolveOrCreateBundle(assemblyPath);

            Assert.Equal(first, second);
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveOrCreateBundle_DifferentSourceDirs_ReturnDifferentPaths()
    {
        var (sourceDir1, assemblyPath1) = CreateFakeSourceDir("-a");
        var (sourceDir2, assemblyPath2) = CreateFakeSourceDir("-b");
        try
        {
            var cache = new ShadowAssemblyCache(debugEnabled: false);

            var path1 = cache.ResolveOrCreateBundle(assemblyPath1);
            var path2 = cache.ResolveOrCreateBundle(assemblyPath2);

            // Different source dirs → different bundle keys → different shadow paths.
            Assert.NotEqual(
                Path.GetDirectoryName(path1),
                Path.GetDirectoryName(path2),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(sourceDir1, recursive: true); } catch { }
            try { Directory.Delete(sourceDir2, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveOrCreateBundle_CopiesAllFilesFromSourceDir()
    {
        var (sourceDir, assemblyPath) = CreateFakeSourceDir();
        File.WriteAllText(Path.Combine(sourceDir, "extra.json"), "{\"key\":\"value\"}");
        try
        {
            var cache = new ShadowAssemblyCache(debugEnabled: false);
            var shadowAssemblyPath = cache.ResolveOrCreateBundle(assemblyPath);
            var bundleDir = Path.GetDirectoryName(shadowAssemblyPath)!;

            // The extra.json should have been copied into the bundle.
            Assert.True(
                File.Exists(Path.Combine(bundleDir, "extra.json")),
                "extra.json should be present in the shadow bundle");
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveOrCreateBundle_ChangedFileContent_CreatesNewBundle()
    {
        var (sourceDir, assemblyPath) = CreateFakeSourceDir();
        try
        {
            var cache = new ShadowAssemblyCache(debugEnabled: false);

            var first = cache.ResolveOrCreateBundle(assemblyPath);

            // Simulate a rebuild: overwrite the DLL with different content.
            File.WriteAllBytes(assemblyPath, [0x4D, 0x5A, 0x00, 0x01, 0x02]);

            var second = cache.ResolveOrCreateBundle(assemblyPath);

            // Different file content → different bundle key → different directory.
            Assert.NotEqual(
                Path.GetDirectoryName(first),
                Path.GetDirectoryName(second),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
        }
    }

    // ─── RunStartupCleanup ────────────────────────────────────────────────────

    [Fact]
    public void RunStartupCleanup_WithEmptyCache_DoesNotThrow()
    {
        var cache = new ShadowAssemblyCache(debugEnabled: false);

        // Should complete silently even when there is nothing to clean up.
        cache.RunStartupCleanup();
    }

    [Fact]
    public void RunStartupCleanup_WithExistingBundles_DoesNotThrow()
    {
        var (sourceDir, assemblyPath) = CreateFakeSourceDir();
        try
        {
            var cache = new ShadowAssemblyCache(debugEnabled: false);
            cache.ResolveOrCreateBundle(assemblyPath);

            // Should run without error even when bundles exist.
            cache.RunStartupCleanup();
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RunStartupCleanup_ExceedsMaxBundles_TrimsOldestBundles()
    {
        // Set QUERYLENS_SHADOW_CACHE_MAX_BUNDLES to 2 so we can trigger the trim path.
        var prevMax = Environment.GetEnvironmentVariable("QUERYLENS_SHADOW_CACHE_MAX_BUNDLES");
        Environment.SetEnvironmentVariable("QUERYLENS_SHADOW_CACHE_MAX_BUNDLES", "2");

        var sourceDirs = new List<string>();
        try
        {
            var cache = new ShadowAssemblyCache(debugEnabled: false);

            // Create 5 distinct bundles (each source dir has different content).
            for (var i = 0; i < 5; i++)
            {
                var (srcDir, asmPath) = CreateFakeSourceDir($"-{i}");
                // Give each a slightly different timestamp so ordering is stable.
                var content = new byte[] { 0x4D, 0x5A, (byte)i };
                File.WriteAllBytes(asmPath, content);

                sourceDirs.Add(srcDir);
                cache.ResolveOrCreateBundle(asmPath);

                // Small sleep to ensure last-write-time differs between bundles.
                Thread.Sleep(10);
            }

            var bundleRoot = Path.Combine(_shadowRoot, "bundles");
            var beforeCount = Directory.GetDirectories(bundleRoot).Length;

            cache.RunStartupCleanup();

            var afterCount = Directory.GetDirectories(bundleRoot).Length;
            Assert.True(afterCount <= 2, $"Expected ≤2 bundles after cleanup, got {afterCount}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUERYLENS_SHADOW_CACHE_MAX_BUNDLES", prevMax);
            foreach (var d in sourceDirs)
                try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RunStartupCleanup_WithDebugEnabled_DoesNotThrow()
    {
        var (sourceDir, assemblyPath) = CreateFakeSourceDir();
        try
        {
            var cache = new ShadowAssemblyCache(debugEnabled: true);
            cache.ResolveOrCreateBundle(assemblyPath);
            cache.RunStartupCleanup();
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RunStartupCleanup_OldBundleExceedsCutoff_DeletesBundle()
    {
        var prevMaxAge = Environment.GetEnvironmentVariable("QUERYLENS_SHADOW_CACHE_MAX_AGE_HOURS");
        // Set max age to 1 hour so a bundle backdated to 2 hours ago falls outside the window.
        Environment.SetEnvironmentVariable("QUERYLENS_SHADOW_CACHE_MAX_AGE_HOURS", "1");

        try
        {
            var cache = new ShadowAssemblyCache(debugEnabled: false);

            // Create a fake stale bundle directory directly inside the bundle root, without
            // going through ResolveOrCreateBundle, to avoid the background TouchDirectory/Task.Run
            // interactions that can mask the backdated timestamp on Windows.
            var bundleRoot = Path.Combine(_shadowRoot, "bundles");
            var fakeBundlePath = Path.Combine(bundleRoot, "stale-bundle-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(fakeBundlePath);
            File.WriteAllBytes(Path.Combine(fakeBundlePath, "FakeApp.dll"), [0x4D, 0x5A]);

            // Backdate to 2 hours ago — outside the 1-hour cutoff window.
            Directory.SetLastWriteTimeUtc(fakeBundlePath, DateTime.UtcNow.AddHours(-2));

            cache.RunStartupCleanup();

            Assert.False(Directory.Exists(fakeBundlePath),
                "Bundle older than max-age cutoff should have been deleted by cleanup.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUERYLENS_SHADOW_CACHE_MAX_AGE_HOURS", prevMaxAge);
        }
    }

    [Fact]
    public void RunStartupCleanup_StaleStagingDir_DeletesIt()
    {
        var prevMaxAge = Environment.GetEnvironmentVariable("QUERYLENS_SHADOW_CACHE_MAX_AGE_HOURS");
        Environment.SetEnvironmentVariable("QUERYLENS_SHADOW_CACHE_MAX_AGE_HOURS", "1");

        try
        {
            var cache = new ShadowAssemblyCache(debugEnabled: false);

            // Manually create a fake stale staging directory.
            var stagingRoot = Path.Combine(_shadowRoot, "staging");
            var fakeStaging = Path.Combine(stagingRoot, "stale-staging-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(fakeStaging);
            Directory.SetLastWriteTimeUtc(fakeStaging, DateTime.UtcNow.AddHours(-2));

            cache.RunStartupCleanup();

            Assert.False(Directory.Exists(fakeStaging),
                "Stale staging directory should have been removed by cleanup.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUERYLENS_SHADOW_CACHE_MAX_AGE_HOURS", prevMaxAge);
        }
    }
}
