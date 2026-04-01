using System.Text;

namespace EFQueryLens.Core.Tests.Lsp;

public class LspProgramHelpersTests : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        Environment.SetEnvironmentVariable("QUERYLENS_LSP_LOG_FILE", null);
    }

    [Fact]
    public void TryRunCacheCommands_ReturnFalse_WithoutFlags()
    {
        Assert.False(LspProgramHelpers.TryRunCacheStatusCommand(["--other"]));
        Assert.False(LspProgramHelpers.TryRunCacheCleanupCommand(["--other"]));
        Assert.False(LspProgramHelpers.TryRunCacheClearCommand(["--other"]));
    }

    [Fact]
    public void ConfigureLspLogWriter_WithoutEnvVar_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("QUERYLENS_LSP_LOG_FILE", null);

        using var writer = LspProgramHelpers.ConfigureLspLogWriter();

        Assert.Null(writer);
    }

    [Fact]
    public void TryRunCacheStatusCommand_WithFlag_PrintsStatus()
    {
        var writer = new StringWriter();
        Console.SetOut(writer);

        var handled = LspProgramHelpers.TryRunCacheStatusCommand(["--cache-status"]);

        Assert.True(handled);
        var output = writer.ToString();
        Assert.Contains("cache-root:", output, StringComparison.Ordinal);
        Assert.Contains("bundle-count:", output, StringComparison.Ordinal);
        Assert.Contains("staging-count:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRunCacheCleanupCommand_WithFlag_PrintsCleanupMessage()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "EFQueryLens", "shadow", "staging");
        Directory.CreateDirectory(rootDir);
        File.WriteAllText(Path.Combine(rootDir, "sample.dll"), "x");

        var writer = new StringWriter();
        Console.SetOut(writer);

        var handled = LspProgramHelpers.TryRunCacheCleanupCommand(["--cache-cleanup"]);

        Assert.True(handled);
        Assert.True(Directory.Exists(rootDir));
        Assert.Empty(Directory.EnumerateFiles(rootDir, "*.dll", SearchOption.AllDirectories));
        Assert.Contains("cache-cleanup:", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryRunCacheClearCommand_WithFlag_RemovesRootFolder()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "EFQueryLens", "shadow");
        Directory.CreateDirectory(rootDir);
        File.WriteAllText(Path.Combine(rootDir, "temp.txt"), "x");

        var writer = new StringWriter();
        Console.SetOut(writer);

        var handled = LspProgramHelpers.TryRunCacheClearCommand(["--cache-clear"]);

        Assert.True(handled);
        Assert.False(Directory.Exists(rootDir));
        Assert.Contains("cache-clear:", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryRunCacheClearCommand_WithFlag_WhenMissing_PrintsNoDirectoryMessage()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "EFQueryLens", "shadow");
        if (Directory.Exists(rootDir))
            Directory.Delete(rootDir, recursive: true);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var handled = LspProgramHelpers.TryRunCacheClearCommand(["--cache-clear"]);

        Assert.True(handled);
        Assert.Contains("cache-clear: no directory", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateEngineAsync_WithFixedUnreachablePort_ThrowsInvalidOperationException()
    {
        var workspace = Path.GetFullPath(".");
        Environment.SetEnvironmentVariable("QUERYLENS_WORKSPACE", workspace);
        Environment.SetEnvironmentVariable("QUERYLENS_DAEMON_PORT", "1");
        Environment.SetEnvironmentVariable("QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS", "100");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await LspProgramHelpers.CreateEngineAsync(debugEnabled: false));

        Assert.Contains("Failed to connect to QueryLens engine", ex.Message, StringComparison.Ordinal);

        Environment.SetEnvironmentVariable("QUERYLENS_DAEMON_PORT", null);
        Environment.SetEnvironmentVariable("QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS", null);
        Environment.SetEnvironmentVariable("QUERYLENS_WORKSPACE", null);
    }

    [Fact]
    public void ConfigureLspLogWriter_WithPath_WritesToLogFile()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"querylens-lsp-test-{Guid.NewGuid():N}.log");
        Environment.SetEnvironmentVariable("QUERYLENS_LSP_LOG_FILE", logPath);

        var writer = LspProgramHelpers.ConfigureLspLogWriter();
        Assert.NotNull(writer);

        Console.Error.WriteLine("test-line");

        writer?.Flush();
        writer?.Dispose();
        Console.SetError(_originalError);

        var text = File.ReadAllText(logPath, Encoding.UTF8);
        Assert.Contains("log-sink enabled", text, StringComparison.Ordinal);
        Assert.Contains("test-line", text, StringComparison.Ordinal);

        File.Delete(logPath);
    }
}
