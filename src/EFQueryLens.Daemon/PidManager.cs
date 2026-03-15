using System.Diagnostics;
using System.Text.Json;
using EFQueryLens.Core.Daemon;

namespace EFQueryLens.Daemon;

internal sealed class PidManager : IDisposable
{
    private readonly string _pidFilePath;

    public PidManager(string workspacePath, string pipeName)
    {
        _pidFilePath = DaemonWorkspaceIdentity.BuildPidFilePath(workspacePath);
        WritePidFile(workspacePath, pipeName);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_pidFilePath))
            {
                File.Delete(_pidFilePath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private void WritePidFile(string workspacePath, string pipeName)
    {
        var directory = Path.GetDirectoryName(_pidFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new DaemonPidInfo
        {
            ProcessId = Process.GetCurrentProcess().Id,
            PipeName = pipeName,
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

    private sealed record DaemonPidInfo
    {
        public int ProcessId { get; init; }
        public string PipeName { get; init; } = string.Empty;
        public string WorkspacePath { get; init; } = string.Empty;
        public string ProcessPath { get; init; } = string.Empty;
        public string AssemblyPath { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public DateTime StartedUtc { get; init; }
    }
}
