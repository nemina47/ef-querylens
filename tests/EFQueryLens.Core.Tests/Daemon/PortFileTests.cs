using EFQueryLens.Daemon;

namespace EFQueryLens.Core.Tests.Daemon;

public class PortFileTests
{
    [Fact]
    public void GetPath_IsStable_AcrossSlashAndCaseNormalization()
    {
        var path1 = PortFile.GetPath("C:/Repo/MyApp");
        var path2 = PortFile.GetPath("c:\\Repo\\MyApp");

        Assert.Equal(path1, path2);
        Assert.EndsWith(".port", path1, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_And_TryDelete_RoundTrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ql-port-{Guid.NewGuid():N}.port");

        await PortFile.WriteAsync(tempFile, 12345);

        Assert.True(File.Exists(tempFile));
        Assert.Equal("12345", await File.ReadAllTextAsync(tempFile));

        PortFile.TryDelete(tempFile);

        Assert.False(File.Exists(tempFile));
    }

    [Fact]
    public void TryDelete_NonExistingPath_DoesNotThrow()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ql-port-{Guid.NewGuid():N}.port");

        PortFile.TryDelete(tempFile);

        Assert.False(File.Exists(tempFile));
    }
}
