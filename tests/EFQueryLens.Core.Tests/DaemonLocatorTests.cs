using System.Diagnostics;
using System.Text.Json;
using EFQueryLens.Core.Daemon;
using EFQueryLens.DaemonClient;

namespace EFQueryLens.Core.Tests;

public class DaemonLocatorTests
{
    [Fact]
    public void TryGetPort_CamelCasePidFile_ReturnsPort()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "querylens-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        var pidFilePath = DaemonWorkspaceIdentity.BuildPidFilePath(workspacePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pidFilePath)!);

            var payload = new
            {
                processId = Process.GetCurrentProcess().Id,
                port = 51234,
                workspacePath = DaemonWorkspaceIdentity.NormalizeWorkspacePath(workspacePath),
            };

            File.WriteAllText(pidFilePath, JsonSerializer.Serialize(payload));

            var port = DaemonLocator.TryGetPort(workspacePath);
            Assert.Equal(51234, port);
        }
        finally
        {
            TryDeleteFile(pidFilePath);
            TryDeleteDirectory(workspacePath);
        }
    }

    [Fact]
    public void TryGetPort_WorkspaceMismatchInPidFile_ReturnsNull()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "querylens-workspace", Guid.NewGuid().ToString("N"));
        var otherWorkspacePath = Path.Combine(Path.GetTempPath(), "querylens-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(otherWorkspacePath);

        var pidFilePath = DaemonWorkspaceIdentity.BuildPidFilePath(workspacePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pidFilePath)!);

            var payload = new
            {
                processId = Process.GetCurrentProcess().Id,
                port = 51234,
                workspacePath = DaemonWorkspaceIdentity.NormalizeWorkspacePath(otherWorkspacePath),
            };

            File.WriteAllText(pidFilePath, JsonSerializer.Serialize(payload));

            var port = DaemonLocator.TryGetPort(workspacePath);
            Assert.Null(port);
        }
        finally
        {
            TryDeleteFile(pidFilePath);
            TryDeleteDirectory(workspacePath);
            TryDeleteDirectory(otherWorkspacePath);
        }
    }

    [Fact]
    public void TryGetPort_RequiredProcessIdMismatch_ReturnsNull()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "querylens-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);

        var pidFilePath = DaemonWorkspaceIdentity.BuildPidFilePath(workspacePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pidFilePath)!);

            var payload = new
            {
                processId = Process.GetCurrentProcess().Id,
                port = 51234,
                workspacePath = DaemonWorkspaceIdentity.NormalizeWorkspacePath(workspacePath),
            };

            File.WriteAllText(pidFilePath, JsonSerializer.Serialize(payload));

            var port = DaemonLocator.TryGetPort(workspacePath, requiredProcessId: Process.GetCurrentProcess().Id + 1);
            Assert.Null(port);
        }
        finally
        {
            TryDeleteFile(pidFilePath);
            TryDeleteDirectory(workspacePath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for test artifacts.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for test artifacts.
        }
    }
}
