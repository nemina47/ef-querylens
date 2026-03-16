using System.Diagnostics;
using System.Text.Json;
using EFQueryLens.Core.Daemon;

namespace EFQueryLens.DaemonClient;

public static partial class DaemonLocator
{
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
                CleanupStaleDaemonInstance(info, pidFilePath, debugLog);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(normalizedExpectedDll)
                && !string.IsNullOrWhiteSpace(normalizedPidAssemblyPath)
                && !string.Equals(normalizedExpectedDll, normalizedPidAssemblyPath, StringComparison.OrdinalIgnoreCase))
            {
                debugLog?.Invoke(
                    $"daemon-discovery stale-runtime reason=assembly-path-mismatch expected='{normalizedExpectedDll}' actual='{normalizedPidAssemblyPath}'");
                CleanupStaleDaemonInstance(info, pidFilePath, debugLog);
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
                CleanupStaleDaemonInstance(info, pidFilePath, debugLog);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(normalizedPidProcessPath)
                && !string.IsNullOrWhiteSpace(processPath)
                && !string.Equals(normalizedPidProcessPath, processPath, StringComparison.OrdinalIgnoreCase))
            {
                debugLog?.Invoke(
                    $"daemon-discovery stale-runtime reason=pid-process-drift pidPath='{normalizedPidProcessPath}' processPath='{processPath}'");
                CleanupStaleDaemonInstance(info, pidFilePath, debugLog);
                return null;
            }

            return info.Port;
        }
        catch
        {
            return null;
        }
    }

    private sealed record DaemonPidInfo
    {
        public int ProcessId { get; init; }
        public int Port { get; init; }
        public string WorkspacePath { get; init; } = string.Empty;
        public string ProcessPath { get; init; } = string.Empty;
        public string AssemblyPath { get; init; } = string.Empty;
        public DateTime StartedUtc { get; init; }
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

    private static void CleanupStaleDaemonInstance(DaemonPidInfo pidInfo, string pidFilePath, Action<string>? debugLog)
    {
        try
        {
            var process = Process.GetProcessById(pidInfo.ProcessId);
            if (!process.HasExited)
            {
                var processName = process.ProcessName;
                var processPath = TryGetProcessPath(process);
                var isDirectDaemonProcess = processName.Equals("EFQueryLens.Daemon", StringComparison.OrdinalIgnoreCase);

                var normalizedPidAssemblyPath = NormalizePathOrNull(pidInfo.AssemblyPath);
                var pidAssemblyName = string.IsNullOrWhiteSpace(normalizedPidAssemblyPath)
                    ? null
                    : Path.GetFileName(normalizedPidAssemblyPath);
                var isDotnetHostedDaemon = processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pidAssemblyName, DaemonAssemblyFileName, StringComparison.OrdinalIgnoreCase)
                    && IsLikelySameRecordedProcess(process, processPath, pidInfo);

                if (isDirectDaemonProcess || isDotnetHostedDaemon)
                {
                    debugLog?.Invoke($"daemon-discovery stale-runtime kill pid={pidInfo.ProcessId} process={processName}");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
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

    private static bool IsLikelySameRecordedProcess(Process process, string? processPath, DaemonPidInfo pidInfo)
    {
        var normalizedPidProcessPath = NormalizePathOrNull(pidInfo.ProcessPath);
        if (!string.IsNullOrWhiteSpace(normalizedPidProcessPath)
            && !string.IsNullOrWhiteSpace(processPath)
            && !string.Equals(normalizedPidProcessPath, processPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (pidInfo.StartedUtc == default)
        {
            return false;
        }

        DateTime processStartedUtc;
        try
        {
            processStartedUtc = process.StartTime.ToUniversalTime();
        }
        catch
        {
            return false;
        }

        var delta = (processStartedUtc - pidInfo.StartedUtc.ToUniversalTime()).Duration();
        return delta <= TimeSpan.FromMinutes(2);
    }
}
