using System.Text;
using EFQueryLens.Core.Common;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Contracts.Explain;
using EFQueryLens.Core.Daemon;
using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests;

public partial class CoreUtilityTests
{
    // ─── DaemonWorkspaceIdentity ──────────────────────────────────────────────

    [Fact]
    public void NormalizeWorkspacePath_TrailingSeparator_IsTrimmed()
    {
        var path = Path.Combine(Path.GetTempPath(), "ql-test-workspace") + Path.DirectorySeparatorChar;
        var result = DaemonWorkspaceIdentity.NormalizeWorkspacePath(path);
        Assert.False(result.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeWorkspacePath_RelativePath_IsResolvedToAbsolute()
    {
        var result = DaemonWorkspaceIdentity.NormalizeWorkspacePath(".");
        Assert.True(Path.IsPathRooted(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeWorkspacePath_NullOrWhitespace_Throws(string? path)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        // and ArgumentException for empty/whitespace — both are ArgumentException subtypes.
        Assert.ThrowsAny<ArgumentException>(() => DaemonWorkspaceIdentity.NormalizeWorkspacePath(path!));
    }

    [Fact]
    public void ComputeWorkspaceHash_SamePath_ReturnsSameHash()
    {
        var path = Path.Combine(Path.GetTempPath(), "ql-test-hash");
        var hash1 = DaemonWorkspaceIdentity.ComputeWorkspaceHash(path);
        var hash2 = DaemonWorkspaceIdentity.ComputeWorkspaceHash(path);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeWorkspaceHash_Returns12HexCharacters()
    {
        var hash = DaemonWorkspaceIdentity.ComputeWorkspaceHash(Path.GetTempPath());
        Assert.Equal(12, hash.Length);
        Assert.Matches("^[0-9a-f]+$", hash);
    }

    [Fact]
    public void ComputeWorkspaceHash_DifferentPaths_ReturnDifferentHashes()
    {
        var hash1 = DaemonWorkspaceIdentity.ComputeWorkspaceHash(@"C:\ProjectA");
        var hash2 = DaemonWorkspaceIdentity.ComputeWorkspaceHash(@"C:\ProjectB");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void BuildPidFilePath_ContainsWorkspaceHashAndJsonExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), "ql-pid-test");
        var hash = DaemonWorkspaceIdentity.ComputeWorkspaceHash(path);

        var pidPath = DaemonWorkspaceIdentity.BuildPidFilePath(path);

        Assert.Contains(hash, pidPath, StringComparison.Ordinal);
        Assert.EndsWith(".json", pidPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPidFilePath_IsUnderQueryLensPidsDirectory()
    {
        var pidPath = DaemonWorkspaceIdentity.BuildPidFilePath(Path.GetTempPath());
        Assert.Contains(".querylens", pidPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pids", pidPath, StringComparison.OrdinalIgnoreCase);
    }

    // ─── TimestampedTextWriter ────────────────────────────────────────────────

    [Fact]
    public void TimestampedTextWriter_WriteLine_PrependsBracketedTimestamp()
    {
        var sb = new StringBuilder();
        using var inner = new StringWriter(sb);
        using var writer = new TimestampedTextWriter(inner);

        writer.WriteLine("hello");

        var output = sb.ToString();
        Assert.StartsWith("[", output, StringComparison.Ordinal);
        Assert.Contains("hello", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimestampedTextWriter_WriteLineAsync_PrependsBracketedTimestamp()
    {
        var sb = new StringBuilder();
        var inner = new StringWriter(sb);
        await using var writer = new TimestampedTextWriter(inner);

        await writer.WriteLineAsync("async-message");

        var output = sb.ToString();
        Assert.StartsWith("[", output, StringComparison.Ordinal);
        Assert.Contains("async-message", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TimestampedTextWriter_WriteChar_DelegatesToInner()
    {
        var sb = new StringBuilder();
        using var inner = new StringWriter(sb);
        using var writer = new TimestampedTextWriter(inner);

        writer.Write('X');

        Assert.Equal("X", sb.ToString());
    }

    [Fact]
    public void TimestampedTextWriter_Encoding_ReturnsInnerEncoding()
    {
        using var inner = new StringWriter();
        using var writer = new TimestampedTextWriter(inner);

        Assert.Equal(inner.Encoding, writer.Encoding);
    }

    [Fact]
    public void TimestampedTextWriter_NullInner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new TimestampedTextWriter(null!));
    }

    // ─── ProjectKeyHelper ─────────────────────────────────────────────────────

    [Fact]
    public void ProjectKeyHelper_GetProjectKey_WhenCsprojInParentDir_ReturnsConsistentHash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ql-projectkey-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write a .csproj in the temp directory
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), "<Project />");

            var sourceFile = Path.Combine(tempDir, "SomeClass.cs");
            File.WriteAllText(sourceFile, "class C {}");

            var key1 = ProjectKeyHelper.GetProjectKey(sourceFile);
            var key2 = ProjectKeyHelper.GetProjectKey(sourceFile);

            Assert.False(string.IsNullOrWhiteSpace(key1));
            Assert.Equal(key1, key2); // Same input → same key (cached)
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ProjectKeyHelper_GetProjectKey_Returns12HexCharacters()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ql-projectkey2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), "<Project />");
            var sourceFile = Path.Combine(tempDir, "SomeClass.cs");
            File.WriteAllText(sourceFile, "class C {}");

            var key = ProjectKeyHelper.GetProjectKey(sourceFile);

            Assert.Equal(12, key.Length);
            Assert.Matches("^[0-9a-f]+$", key);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ProjectKeyHelper_GetProjectKey_WhenNoCsproj_ReturnsFallbackHash()
    {
        // Use a path in a temp subdir that has no .csproj anywhere in the chain
        // (within the isolated temp subdirectory — the real temp dir won't have one either)
        var isolated = Path.Combine(Path.GetTempPath(), "ql-noproj-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(isolated);

        try
        {
            var sourceFile = Path.Combine(isolated, "NoProject.cs");
            File.WriteAllText(sourceFile, "class C {}");

            var key = ProjectKeyHelper.GetProjectKey(sourceFile);

            // Should return a non-empty fallback hash — length 12 hex chars
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.Equal(12, key.Length);
        }
        finally
        {
            try { Directory.Delete(isolated, recursive: true); } catch { /* best-effort */ }
        }
    }

}
