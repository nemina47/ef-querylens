using System.Security.Cryptography;
using System.Text;
using EFQueryLens.Lsp.Engine;

namespace EFQueryLens.Integration.Tests.Lsp;

/// <summary>
/// Tests for the pure/deterministic helpers in <see cref="EngineDiscovery"/>.
/// No live process or network access is required.
/// </summary>
public class EngineDiscoveryTests : IDisposable
{
    private readonly string _originalWorkspace;
    private readonly string _originalDaemonWorkspace;

    public EngineDiscoveryTests()
    {
        _originalWorkspace = Environment.GetEnvironmentVariable("QUERYLENS_WORKSPACE") ?? string.Empty;
        _originalDaemonWorkspace =
            Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_WORKSPACE") ?? string.Empty;
    }

    public void Dispose()
    {
        SetEnv("QUERYLENS_WORKSPACE", _originalWorkspace);
        SetEnv("QUERYLENS_DAEMON_WORKSPACE", _originalDaemonWorkspace);
    }

    // ── GetPortFilePath ──────────────────────────────────────────────────────

    [Fact]
    public void GetPortFilePath_SamePath_ReturnsSamePath()
    {
        var path = @"C:\Projects\MyApp";

        var result1 = EngineDiscovery.GetPortFilePath(path);
        var result2 = EngineDiscovery.GetPortFilePath(path);

        Assert.Equal(result1, result2);
    }

    [Fact]
    public void GetPortFilePath_DifferentPaths_ReturnDifferentFiles()
    {
        var result1 = EngineDiscovery.GetPortFilePath(@"C:\Projects\AppA");
        var result2 = EngineDiscovery.GetPortFilePath(@"C:\Projects\AppB");

        Assert.NotEqual(result1, result2);
    }

    [Fact]
    public void GetPortFilePath_ResultIsInTempDir()
    {
        var result = EngineDiscovery.GetPortFilePath(@"C:\Projects\MyApp");

        Assert.StartsWith(Path.GetTempPath(), result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetPortFilePath_ResultMatchesExpectedPattern()
    {
        var result = EngineDiscovery.GetPortFilePath(@"C:\Projects\MyApp");
        var fileName = Path.GetFileName(result);

        Assert.Matches(@"^querylens-[0-9a-f]{12}\.port$", fileName);
    }

    [Fact]
    public void GetPortFilePath_HashIsFirstTwelveHexCharsOfSha256()
    {
        const string workspacePath = @"C:\Projects\MyApp";

        // Reproduce internal hash: lower-case + forward slashes → SHA-256 → first 12 hex chars
        var normalized = workspacePath.ToLowerInvariant().Replace('\\', '/');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var expectedHash = Convert.ToHexString(bytes)[..12].ToLowerInvariant();

        var portFile = EngineDiscovery.GetPortFilePath(workspacePath);
        var fileName = Path.GetFileName(portFile);

        Assert.Equal($"querylens-{expectedHash}.port", fileName);
    }

    [Fact]
    public void GetPortFilePath_PathCaseInsensitive_SameHashOnWindows()
    {
        // The implementation lower-cases before hashing, so these must match.
        var lower = EngineDiscovery.GetPortFilePath(@"c:\projects\myapp");
        var upper = EngineDiscovery.GetPortFilePath(@"C:\PROJECTS\MYAPP");

        Assert.Equal(lower, upper);
    }

    // ── ResolveWorkspacePath ─────────────────────────────────────────────────

    [Fact]
    public void ResolveWorkspacePath_WithQUERYLENS_WORKSPACE_ReturnsFullPath()
    {
        var dir = Path.GetTempPath(); // guaranteed to exist
        SetEnv("QUERYLENS_WORKSPACE", dir);
        SetEnv("QUERYLENS_DAEMON_WORKSPACE", string.Empty);

        var result = EngineDiscovery.ResolveWorkspacePath();

        Assert.Equal(Path.GetFullPath(dir), result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveWorkspacePath_WithQUERYLENS_DAEMON_WORKSPACE_ReturnsFullPath()
    {
        var dir = Path.GetTempPath();
        SetEnv("QUERYLENS_WORKSPACE", string.Empty);
        SetEnv("QUERYLENS_DAEMON_WORKSPACE", dir);

        var result = EngineDiscovery.ResolveWorkspacePath();

        Assert.Equal(Path.GetFullPath(dir), result, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveWorkspacePath_BothEnvVarsEmpty_ReturnsCwdOrNull()
    {
        SetEnv("QUERYLENS_WORKSPACE", string.Empty);
        SetEnv("QUERYLENS_DAEMON_WORKSPACE", string.Empty);

        // CWD exists → returns a non-null path; otherwise null.
        var result = EngineDiscovery.ResolveWorkspacePath();

        if (result is not null)
        {
            Assert.True(Directory.Exists(result));
        }
    }

    [Fact]
    public void ResolveWorkspacePath_QUERYLENS_WORKSPACE_TakesPrecedenceOverDaemonWorkspace()
    {
        var primaryDir = Path.GetTempPath();
        var daemonDir = Path.Combine(Path.GetTempPath(), "daemon-workspace");

        SetEnv("QUERYLENS_WORKSPACE", primaryDir);
        SetEnv("QUERYLENS_DAEMON_WORKSPACE", daemonDir);

        var result = EngineDiscovery.ResolveWorkspacePath();

        Assert.Equal(Path.GetFullPath(primaryDir), result, StringComparer.OrdinalIgnoreCase);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void SetEnv(string name, string value) =>
        Environment.SetEnvironmentVariable(name, string.IsNullOrEmpty(value) ? null : value);
}
