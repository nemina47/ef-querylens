using System.Diagnostics;
using System.Text.Json;
using EFQueryLens.Core.Daemon;

namespace EFQueryLens.Daemon;

internal sealed class PidManager : IDisposable
{
    private readonly string _pidFilePath;
    private readonly int _processId;
    private static readonly JsonSerializerOptions s_pidJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public PidManager(string workspacePath, int port)
    {
        _pidFilePath = DaemonWorkspaceIdentity.BuildPidFilePath(workspacePath);
        _processId = Process.GetCurrentProcess().Id;
        WritePidFile(workspacePath, port);
    }

    public void Dispose()
    {
        try
        {
            if (!File.Exists(_pidFilePath))
            {
                return;
            }

            // Prevent an older daemon instance from deleting a newer daemon's pid file.
            var currentOwnerProcessId = TryReadPidFileOwnerProcessId(_pidFilePath);
            if (currentOwnerProcessId == _processId)
            {
                File.Delete(_pidFilePath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private void WritePidFile(string workspacePath, int port)
    {
        var directory = Path.GetDirectoryName(_pidFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new DaemonPidInfo
        {
            ProcessId = _processId,
            Port = port,
            WorkspacePath = DaemonWorkspaceIdentity.NormalizeWorkspacePath(workspacePath),
            ProcessPath = Environment.ProcessPath ?? string.Empty,
            AssemblyPath = typeof(PidManager).Assembly.Location,
            Version = typeof(PidManager).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            StartedUtc = DateTime.UtcNow,
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });

        File.WriteAllText(_pidFilePath, json);
    }

    private static int? TryReadPidFileOwnerProcessId(string pidFilePath)
    {
        try
        {
            var json = File.ReadAllText(pidFilePath);
            var payload = JsonSerializer.Deserialize<DaemonPidInfo>(json, s_pidJsonOptions);
            if (payload is null || payload.ProcessId <= 0)
            {
                return null;
            }

            return payload.ProcessId;
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
        public string Version { get; init; } = string.Empty;
        public DateTime StartedUtc { get; init; }
    }
}
