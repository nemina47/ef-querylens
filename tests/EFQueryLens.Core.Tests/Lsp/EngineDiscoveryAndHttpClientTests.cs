using System.Net.Http.Json;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Engine;

namespace EFQueryLens.Core.Tests.Lsp;

public sealed class EngineDiscoveryAndHttpClientTests : IAsyncDisposable
{
    private readonly List<string> _workspacesToClean = [];

    [Fact]
    public async Task StartEngineAsync_ThenTryGetExistingPortAsync_ReturnsRunningPort()
    {
        var workspace = CreateWorkspace();
        var daemonPath = GetDaemonAssemblyPath();

        var port = await EngineDiscovery.StartEngineAsync(workspace, daemonPath, timeoutMs: 15000);
        var discoveredPort = await EngineDiscovery.TryGetExistingPortAsync(workspace, pingTimeoutMs: 2000);

        Assert.NotNull(port);
        Assert.Equal(port, discoveredPort);

        using var http = new HttpClient();
        var response = await http.GetAsync($"http://127.0.0.1:{port}/ping");
        Assert.True(response.IsSuccessStatusCode);

        await ShutdownDaemonAsync(workspace);
    }

    [Fact]
    public async Task GetOrStartEngineAsync_ReusesExistingEngine_ForWorkspace()
    {
        var workspace = CreateWorkspace();
        var daemonPath = GetDaemonAssemblyPath();

        var firstPort = await EngineDiscovery.GetOrStartEngineAsync(workspace, daemonPath, timeoutMs: 15000, debugLog: false);
        var secondPort = await EngineDiscovery.GetOrStartEngineAsync(workspace, daemonPath, timeoutMs: 15000, debugLog: false);

        Assert.Equal(firstPort, secondPort);

        var portFile = EngineDiscovery.GetPortFilePath(workspace);
        Assert.True(File.Exists(portFile));
        Assert.Equal(firstPort.ToString(), (await File.ReadAllTextAsync(portFile)).Trim());

        await ShutdownDaemonAsync(workspace);
    }

    [Fact]
    public async Task EngineHttpClient_ControlOperations_WorkAgainstRealDaemon()
    {
        var workspace = CreateWorkspace();
        var daemonPath = GetDaemonAssemblyPath();
        var port = await EngineDiscovery.StartEngineAsync(workspace, daemonPath, timeoutMs: 15000)
            ?? throw new InvalidOperationException("Daemon did not start.");

        await using var client = new EngineHttpClient(port, workspace, daemonPath, startTimeoutMs: 15000, debugEnabled: false);

        await client.PingAsync();
        await client.InvalidateCacheAsync();
        await client.WarmTranslateAsync(new TranslationRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "C:/app/Fake.dll",
            ContextVariableName = "db",
            LocalSymbolGraph = [],
            V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
            {
                Expression = "db.Orders",
                ContextVariableName = "db",
                RootContextVariableName = "db",
                BoundaryKind = "Queryable",
                NeedsMaterialization = false,
            },
            V2CapturePlan = new V2CapturePlanSnapshot
            {
                ExecutableExpression = "db.Orders",
                IsComplete = true,
            },
        });

        await client.RestartAsync();
        await client.PingAsync();

        await ShutdownDaemonAsync(workspace);
    }

    [Fact]
    public async Task ResolveWorkspaceAndEnginePath_UseEnvironmentOverrides()
    {
        var workspace = CreateWorkspace();
        var daemonPath = GetDaemonAssemblyPath();
        var originalWorkspace = Environment.GetEnvironmentVariable("QUERYLENS_WORKSPACE");
        var originalDaemonPath = Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_DLL");

        try
        {
            Environment.SetEnvironmentVariable("QUERYLENS_WORKSPACE", workspace);
            Environment.SetEnvironmentVariable("QUERYLENS_DAEMON_DLL", daemonPath);

            Assert.Equal(workspace, EngineDiscovery.ResolveWorkspacePath());
            Assert.Equal(daemonPath, EngineDiscovery.ResolveEngineAssemblyPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUERYLENS_WORKSPACE", originalWorkspace);
            Environment.SetEnvironmentVariable("QUERYLENS_DAEMON_DLL", originalDaemonPath);
        }
    }

    [Fact]
    public async Task TryGetExistingPortAsync_WithInvalidPortFile_ReturnsNull()
    {
        var workspace = CreateWorkspace();
        var portFile = EngineDiscovery.GetPortFilePath(workspace);
        await File.WriteAllTextAsync(portFile, "not-a-port");

        var port = await EngineDiscovery.TryGetExistingPortAsync(workspace, pingTimeoutMs: 200);

        Assert.Null(port);
    }

    [Fact]
    public void BuildEngineStartInfo_WithMissingAssembly_ReturnsNull()
    {
        var result = InvokeBuildEngineStartInfo("C:/repo", string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void BuildEngineStartInfo_WithAdjacentExe_PrefersExecutable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ql-startinfo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dllPath = Path.Combine(tempDir, "EFQueryLens.Daemon.dll");
            var exePath = Path.Combine(tempDir, "EFQueryLens.Daemon.exe");
            File.WriteAllText(dllPath, string.Empty);
            File.WriteAllText(exePath, string.Empty);

            var result = InvokeBuildEngineStartInfo("C:/repo", dllPath);

            Assert.NotNull(result);
            Assert.Equal(exePath, result!.FileName);
            Assert.Contains("--workspace", result.Arguments, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EngineJsonOptions_TimeSpanConverters_RoundTripTicks()
    {
        var payload = new EngineTimingPayload
        {
            Duration = TimeSpan.FromSeconds(2),
            OptionalDuration = TimeSpan.FromMilliseconds(50),
        };

        var json = JsonSerializer.Serialize(payload, EngineJsonOptions.Default);
        var roundTrip = JsonSerializer.Deserialize<EngineTimingPayload>(json, EngineJsonOptions.Default);

        Assert.Contains("20000000", json, StringComparison.Ordinal);
        Assert.Contains("500000", json, StringComparison.Ordinal);
        Assert.NotNull(roundTrip);
        Assert.Equal(payload.Duration, roundTrip.Duration);
        Assert.Equal(payload.OptionalDuration, roundTrip.OptionalDuration);
    }

    [Fact]
    public async Task PumpStderrAsync_ForProcess_WritesLinesToLogger()
    {
        var logs = new List<string>();
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c echo stderr-line 1>&2",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            }
            : new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = "-c \"echo stderr-line 1>&2\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start test process.");

        await InvokePumpStderrAsync(process, message => logs.Add(message));
        await process.WaitForExitAsync();

        Assert.Contains(logs, message => message.Contains("stderr-line", StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var workspace in _workspacesToClean)
        {
            try
            {
                await ShutdownDaemonAsync(workspace);
            }
            catch
            {
            }

            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch
            {
            }
        }
    }

    private string CreateWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"ql-engine-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        _workspacesToClean.Add(workspace);
        return workspace;
    }

    private static string GetDaemonAssemblyPath()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "EFQueryLens.Daemon", "bin", "Debug", "net10.0", "EFQueryLens.Daemon.dll"));

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Daemon assembly not found for engine discovery tests.", path);
        }

        return path;
    }

    private static ProcessStartInfo? InvokeBuildEngineStartInfo(string workspacePath, string engineAssemblyPath)
    {
        var method = typeof(EngineDiscovery).GetMethod("BuildEngineStartInfo", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildEngineStartInfo not found.");
        return (ProcessStartInfo?)method.Invoke(null, [workspacePath, engineAssemblyPath]);
    }

    private static async Task InvokePumpStderrAsync(Process process, Action<string> log)
    {
        var method = typeof(EngineDiscovery).GetMethod("PumpStderrAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PumpStderrAsync not found.");
        var task = (Task)method.Invoke(null, [process, log])!;
        await task;
    }

    private static async Task ShutdownDaemonAsync(string workspace)
    {
        var port = await EngineDiscovery.TryGetExistingPortAsync(workspace, pingTimeoutMs: 500);
        if (port is null)
        {
            return;
        }

        using var http = new HttpClient();
        try
        {
            await http.PostAsync($"http://127.0.0.1:{port}/shutdown", null);
        }
        catch
        {
        }

        await WaitForPortShutdownAsync(port.Value);
    }

    private static async Task WaitForPortShutdownAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync($"http://127.0.0.1:{port}/ping");
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    private sealed class EngineTimingPayload
    {
        public TimeSpan Duration { get; init; }
        public TimeSpan? OptionalDuration { get; init; }
    }
}