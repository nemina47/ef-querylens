using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EFQueryLens.Lsp.Engine;

/// <summary>
/// Finds or starts the QueryLens engine process and returns its HTTP port.
/// Uses a port-only file ({tmpdir}/querylens-{workspaceHash}.port) for shared discovery.
/// Multiple LSP instances sharing the same workspace reuse the same engine.
/// </summary>
internal static partial class EngineDiscovery
{
    private const string EngineAssemblyFileName = "EFQueryLens.Daemon.dll";
    private const string EngineExecutableFileName = "EFQueryLens.Daemon";

    [GeneratedRegex(@"QUERYLENS_PORT=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PortAnnouncementPattern();

    /// <summary>
    /// Returns the workspace path from env vars or current directory.
    /// </summary>
    public static string? ResolveWorkspacePath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("QUERYLENS_WORKSPACE"),
            Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_WORKSPACE"),
            Directory.GetCurrentDirectory(),
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

    /// <summary>
    /// Returns the engine assembly .dll path (adjacent to LSP assembly, or via env var).
    /// </summary>
    public static string? ResolveEngineAssemblyPath()
    {
        var envPath = Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_DLL");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            try
            {
                var fullPath = Path.GetFullPath(envPath);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
                // Ignore invalid env var path.
            }
        }

        var candidateInBaseDir = Path.Combine(AppContext.BaseDirectory, EngineAssemblyFileName);
        if (File.Exists(candidateInBaseDir))
        {
            return candidateInBaseDir;
        }

        return null;
    }

    /// <summary>
    /// Returns the port file path for the given workspace.
    /// </summary>
    public static string GetPortFilePath(string workspacePath)
    {
        var hash = WorkspaceHash(workspacePath);
        return Path.Combine(Path.GetTempPath(), $"querylens-{hash}.port");
    }

    /// <summary>
    /// Tries to read the port file and ping the engine. Returns the port on success, or null.
    /// </summary>
    public static async Task<int?> TryGetExistingPortAsync(
        string workspacePath,
        int pingTimeoutMs = 1000,
        Action<string>? debugLog = null)
    {
        var portFile = GetPortFilePath(workspacePath);
        if (!File.Exists(portFile))
        {
            return null;
        }

        try
        {
            var text = await File.ReadAllTextAsync(portFile);
            if (!int.TryParse(text.Trim(), out var port) || port <= 0)
            {
                debugLog?.Invoke($"engine-discovery port-file-invalid path={portFile} content={text.Trim()}");
                return null;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(pingTimeoutMs) };
            var response = await http.GetAsync($"http://127.0.0.1:{port}/ping");
            if (response.IsSuccessStatusCode)
            {
                debugLog?.Invoke($"engine-discovery found-existing port={port}");
                return port;
            }

            debugLog?.Invoke($"engine-discovery ping-failed port={port} status={(int)response.StatusCode}");
            return null;
        }
        catch (Exception ex)
        {
            debugLog?.Invoke($"engine-discovery ping-error portFile={portFile} type={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Starts a new engine process and waits for it to announce its port on stdout.
    /// </summary>
    public static async Task<int?> StartEngineAsync(
        string workspacePath,
        string engineAssemblyPath,
        int timeoutMs,
        Action<string>? debugLog = null)
    {
        var startInfo = BuildEngineStartInfo(workspacePath, engineAssemblyPath);
        if (startInfo is null)
        {
            debugLog?.Invoke($"engine-start skipped reason=launch-target-not-found assemblyPath={engineAssemblyPath}");
            return null;
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                debugLog?.Invoke("engine-start failed reason=process-start-returned-null");
                return null;
            }

            debugLog?.Invoke($"engine-start started pid={process.Id} workspace={workspacePath}");

            // Pump stderr to debugLog in background.
            _ = PumpStderrAsync(process, debugLog);

            // Read stdout lines looking for the port announcement.
            using var cts = new CancellationTokenSource(timeoutMs);
            var portPattern = PortAnnouncementPattern();

            while (!cts.Token.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    // EOF — process ended before announcing port.
                    debugLog?.Invoke($"engine-start stdout-eof pid={process.Id}");
                    break;
                }

                debugLog?.Invoke($"[QL-Engine] {line}");

                var match = portPattern.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var port) && port > 0)
                {
                    debugLog?.Invoke($"engine-start ready port={port} pid={process.Id}");
                    return port;
                }
            }

            debugLog?.Invoke($"engine-start timeout workspace={workspacePath} timeoutMs={timeoutMs}");
            return null;
        }
        catch (Exception ex)
        {
            debugLog?.Invoke($"engine-start failed type={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Orchestrates engine acquisition: try existing → acquire advisory lock → double-check → start → write port.
    /// </summary>
    public static async Task<int> GetOrStartEngineAsync(
        string workspacePath,
        string engineAssemblyPath,
        int timeoutMs,
        bool debugLog,
        Action<string>? log = null)
    {
        Action<string>? logger = debugLog ? log : null;

        // Fast path: engine is already running.
        var existingPort = await TryGetExistingPortAsync(workspacePath, debugLog: logger);
        if (existingPort is not null)
        {
            return existingPort.Value;
        }

        // Acquire an advisory lock file so only one LSP instance starts the engine.
        var portFile = GetPortFilePath(workspacePath);
        var lockFile = portFile + ".lock";
        FileStream? lockStream = null;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (lockStream is null && DateTime.UtcNow < deadline)
        {
            try
            {
                lockStream = File.Open(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Another process holds the lock — wait a bit then retry.
                logger?.Invoke("engine-discovery lock-contention waiting...");
                await Task.Delay(100);
            }
        }

        await using var lockDispose = lockStream;

        // Double-check: another process may have started the engine while we waited for the lock.
        var doubleCheckPort = await TryGetExistingPortAsync(workspacePath, debugLog: logger);
        if (doubleCheckPort is not null)
        {
            return doubleCheckPort.Value;
        }

        // Still not running — start it ourselves.
        var remainingMs = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
        var newPort = await StartEngineAsync(workspacePath, engineAssemblyPath, remainingMs, logger);

        if (newPort is null)
        {
            throw new InvalidOperationException(
                $"Failed to start QueryLens engine for workspace '{workspacePath}'.");
        }

        // Write port to the port file so other LSP instances can reuse this engine.
        try
        {
            await File.WriteAllTextAsync(portFile, newPort.Value.ToString());
            logger?.Invoke($"engine-discovery wrote-port-file path={portFile} port={newPort.Value}");
        }
        catch (Exception ex)
        {
            logger?.Invoke($"engine-discovery port-file-write-failed path={portFile} type={ex.GetType().Name} message={ex.Message}");
        }

        return newPort.Value;
    }

    // --- Private helpers ---

    private static string WorkspaceHash(string workspacePath)
    {
        var normalized = workspacePath
            .ToLowerInvariant()
            .Replace('\\', '/');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static ProcessStartInfo? BuildEngineStartInfo(string workspacePath, string engineAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(engineAssemblyPath))
        {
            return null;
        }

        // Prefer a self-contained executable adjacent to the .dll if one exists.
        var directory = Path.GetDirectoryName(engineAssemblyPath) ?? AppContext.BaseDirectory;
        var exeCandidate = Path.Combine(directory, EngineExecutableFileName + ".exe");
        var bareCandidate = Path.Combine(directory, EngineExecutableFileName);

        if (File.Exists(exeCandidate))
        {
            return new ProcessStartInfo
            {
                FileName = exeCandidate,
                Arguments = $"--workspace \"{workspacePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
        }

        if (File.Exists(bareCandidate))
        {
            return new ProcessStartInfo
            {
                FileName = bareCandidate,
                Arguments = $"--workspace \"{workspacePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
        }

        if (File.Exists(engineAssemblyPath))
        {
            return new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{engineAssemblyPath}\" --workspace \"{workspacePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
        }

        return null;
    }

    private static async Task PumpStderrAsync(Process process, Action<string>? debugLog)
    {
        if (debugLog is null)
        {
            return;
        }

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
                    debugLog($"[QL-Engine] {line}");
                }
            }
        }
        catch
        {
            // Best-effort stderr pump; failures do not affect startup.
        }
    }
}
