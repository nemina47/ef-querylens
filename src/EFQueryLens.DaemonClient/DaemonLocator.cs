using System.Diagnostics;
using System.Text.Json;
using EFQueryLens.Core.Daemon;

namespace EFQueryLens.DaemonClient;

public static class DaemonLocator
{
    private const string DaemonExecutableFileName = "EFQueryLens.Daemon";
    private const string DaemonAssemblyFileName = "EFQueryLens.Daemon.dll";
    private static readonly JsonSerializerOptions s_pidJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string? ResolveWorkspacePath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("QUERYLENS_WORKSPACE"),
            Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_WORKSPACE"),
            Environment.GetEnvironmentVariable("QUERYLENS_REPOSITORY_ROOT"),
            Environment.CurrentDirectory,
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (Directory.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
                // Ignore invalid path candidates.
            }
        }

        return null;
    }

    public static string? ResolveDaemonAssemblyPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var explicitFullPath = Path.GetFullPath(explicitPath);
            if (File.Exists(explicitFullPath))
            {
                return explicitFullPath;
            }
        }

        var envPath = Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_DLL");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var envFullPath = Path.GetFullPath(envPath);
            if (File.Exists(envFullPath))
            {
                return envFullPath;
            }
        }

        var candidateInBaseDir = Path.Combine(AppContext.BaseDirectory, DaemonAssemblyFileName);
        if (File.Exists(candidateInBaseDir))
        {
            return candidateInBaseDir;
        }

        return null;
    }

    public static string? ResolveDaemonExecutablePath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var explicitFullPath = Path.GetFullPath(explicitPath);
            if (File.Exists(explicitFullPath))
            {
                return explicitFullPath;
            }
        }

        var envPath = Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_EXE");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var envFullPath = Path.GetFullPath(envPath);
            if (File.Exists(envFullPath))
            {
                return envFullPath;
            }
        }

        var candidateFileNames = new[]
        {
            DaemonExecutableFileName,
            $"{DaemonExecutableFileName}.exe",
        };

        foreach (var fileName in candidateFileNames)
        {
            var candidateInBaseDir = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(candidateInBaseDir))
            {
                return candidateInBaseDir;
            }
        }

        return null;
    }

    public static int? TryGetPort(
        string workspacePath,
        string? expectedDaemonExecutablePath = null,
        string? expectedDaemonAssemblyPath = null,
        Action<string>? debugLog = null,
        int? requiredProcessId = null)
    {
        var normalizedWorkspacePath = DaemonWorkspaceIdentity.NormalizeWorkspacePath(workspacePath);
        var pidFilePath = DaemonWorkspaceIdentity.BuildPidFilePath(normalizedWorkspacePath);
        if (!File.Exists(pidFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(pidFilePath);
            var info = JsonSerializer.Deserialize<DaemonPidInfo>(json, s_pidJsonOptions);
            if (info is null
                || info.ProcessId <= 0
                || info.Port <= 0)
            {
                return null;
            }

            var normalizedExpectedExe = NormalizePathOrNull(expectedDaemonExecutablePath);
            var normalizedExpectedDll = NormalizePathOrNull(expectedDaemonAssemblyPath);
            var normalizedPidProcessPath = NormalizePathOrNull(info.ProcessPath);
            var normalizedPidAssemblyPath = NormalizePathOrNull(info.AssemblyPath);

            if (!string.IsNullOrWhiteSpace(info.WorkspacePath))
            {
                var normalizedPidWorkspacePath = DaemonWorkspaceIdentity.NormalizeWorkspacePath(info.WorkspacePath);
                if (!string.Equals(
                        normalizedWorkspacePath,
                        normalizedPidWorkspacePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            if (requiredProcessId is > 0 && info.ProcessId != requiredProcessId.Value)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(normalizedExpectedExe)
                && !string.IsNullOrWhiteSpace(normalizedPidProcessPath)
                && !string.Equals(normalizedExpectedExe, normalizedPidProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                debugLog?.Invoke(
                    $"daemon-discovery stale-runtime reason=process-path-mismatch expected='{normalizedExpectedExe}' actual='{normalizedPidProcessPath}'");
                CleanupStaleDaemonInstance(info.ProcessId, pidFilePath, debugLog);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(normalizedExpectedDll)
                && !string.IsNullOrWhiteSpace(normalizedPidAssemblyPath)
                && !string.Equals(normalizedExpectedDll, normalizedPidAssemblyPath, StringComparison.OrdinalIgnoreCase))
            {
                debugLog?.Invoke(
                    $"daemon-discovery stale-runtime reason=assembly-path-mismatch expected='{normalizedExpectedDll}' actual='{normalizedPidAssemblyPath}'");
                CleanupStaleDaemonInstance(info.ProcessId, pidFilePath, debugLog);
                return null;
            }

            var process = Process.GetProcessById(info.ProcessId);
            var processPath = TryGetProcessPath(process);

            if (!string.IsNullOrWhiteSpace(normalizedExpectedExe)
                && !string.IsNullOrWhiteSpace(processPath)
                && !string.Equals(normalizedExpectedExe, processPath, StringComparison.OrdinalIgnoreCase))
            {
                debugLog?.Invoke(
                    $"daemon-discovery stale-runtime reason=live-process-path-mismatch expected='{normalizedExpectedExe}' actual='{processPath}'");
                CleanupStaleDaemonInstance(info.ProcessId, pidFilePath, debugLog);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(normalizedPidProcessPath)
                && !string.IsNullOrWhiteSpace(processPath)
                && !string.Equals(normalizedPidProcessPath, processPath, StringComparison.OrdinalIgnoreCase))
            {
                debugLog?.Invoke(
                    $"daemon-discovery stale-runtime reason=pid-process-drift pidPath='{normalizedPidProcessPath}' processPath='{processPath}'");
                CleanupStaleDaemonInstance(info.ProcessId, pidFilePath, debugLog);
                return null;
            }

            return info.Port;
        }
        catch
        {
            return null;
        }
    }

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

    private sealed record DaemonPidInfo
    {
        public int ProcessId { get; init; }
        public int Port { get; init; }
        public string WorkspacePath { get; init; } = string.Empty;
        public string ProcessPath { get; init; } = string.Empty;
        public string AssemblyPath { get; init; } = string.Empty;
    }

    private static string? NormalizePathOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return NormalizePathOrNull(path);
        }
        catch
        {
            return null;
        }
    }

    private static void CleanupStaleDaemonInstance(int processId, string pidFilePath, Action<string>? debugLog)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            if (!process.HasExited
                && process.ProcessName.Equals("EFQueryLens.Daemon", StringComparison.OrdinalIgnoreCase))
            {
                debugLog?.Invoke($"daemon-discovery stale-runtime kill pid={processId}");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // Best-effort cleanup; stale process may already be gone.
        }

        try
        {
            if (File.Exists(pidFilePath))
            {
                File.Delete(pidFilePath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
