using System.Diagnostics;
using EFQueryLens.Core.Daemon;

namespace EFQueryLens.DaemonClient;

public static partial class DaemonLocator
{
    public static async Task<int?> TryGetOrStartDaemonAsync(
        string workspacePath,
        string? daemonExecutablePath,
        string? daemonAssemblyPath,
        int timeoutMilliseconds,
        Action<string>? debugLog = null,
        bool forceFreshStart = false,
        CancellationToken ct = default)
    {
        var normalizedWorkspacePath = DaemonWorkspaceIdentity.NormalizeWorkspacePath(workspacePath);
        if (!forceFreshStart)
        {
            var existingPort = TryGetPort(
                normalizedWorkspacePath,
                expectedDaemonExecutablePath: daemonExecutablePath,
                expectedDaemonAssemblyPath: daemonAssemblyPath,
                debugLog: debugLog);
            if (existingPort is > 0)
            {
                return existingPort;
            }
        }
        else
        {
            debugLog?.Invoke($"daemon-autostart force-fresh workspace={normalizedWorkspacePath}");
        }

        var startInfo = CreateDaemonStartInfo(
            workspacePath: normalizedWorkspacePath,
            daemonExecutablePath: daemonExecutablePath,
            daemonAssemblyPath: daemonAssemblyPath);

        if (startInfo is null)
        {
            debugLog?.Invoke("daemon-autostart skipped reason=daemon-launch-target-not-found");
            return null;
        }

        try
        {
            startInfo.RedirectStandardError = debugLog is not null;

            if (debugLog is not null)
            {
                startInfo.Environment["QUERYLENS_DEBUG"] = "1";
            }

            var process = Process.Start(startInfo);
            if (process is null)
            {
                debugLog?.Invoke("daemon-autostart failed reason=process-start-returned-null");
                return null;
            }

            debugLog?.Invoke($"daemon-autostart started pid={process.Id} workspace={normalizedWorkspacePath}");
            if (debugLog is not null)
            {
                _ = PumpDaemonErrorStreamAsync(process, debugLog);
            }

            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadlineUtc)
            {
                ct.ThrowIfCancellationRequested();

                var discoveredPort = TryGetPort(
                    normalizedWorkspacePath,
                    expectedDaemonExecutablePath: daemonExecutablePath,
                    expectedDaemonAssemblyPath: daemonAssemblyPath,
                    debugLog: debugLog,
                    requiredProcessId: process.Id);
                if (discoveredPort is > 0)
                {
                    debugLog?.Invoke($"daemon-autostart ready port={discoveredPort.Value}");
                    return discoveredPort;
                }

                await Task.Delay(150, ct);
            }

            debugLog?.Invoke($"daemon-autostart timeout workspace={normalizedWorkspacePath} timeoutMs={timeoutMilliseconds}");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            debugLog?.Invoke($"daemon-autostart failed reason={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }

    private static ProcessStartInfo? CreateDaemonStartInfo(
        string workspacePath,
        string? daemonExecutablePath,
        string? daemonAssemblyPath)
    {
        if (!string.IsNullOrWhiteSpace(daemonExecutablePath) && File.Exists(daemonExecutablePath))
        {
            return new ProcessStartInfo
            {
                FileName = daemonExecutablePath,
                Arguments = $"--workspace \"{workspacePath}\" --port 0",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };
        }

        if (!string.IsNullOrWhiteSpace(daemonAssemblyPath) && File.Exists(daemonAssemblyPath))
        {
            return new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{daemonAssemblyPath}\" --workspace \"{workspacePath}\" --port 0",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };
        }

        return null;
    }

    private static async Task PumpDaemonErrorStreamAsync(Process process, Action<string> debugLog)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (line.Length > 0)
                {
                    debugLog($"[QL-Daemon] {line}");
                }
            }
        }
        catch
        {
            // Ignore stderr pump failures. Startup path has timeout handling.
        }
    }
}
