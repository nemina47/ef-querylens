using System.Diagnostics;
using System.Text.Json;
using EFQueryLens.Core;
using EFQueryLens.Core.Daemon;
using EFQueryLens.Core.Grpc;
using EFQueryLens.DaemonClient;
using Grpc.Core;
using Grpc.Net.Client;

namespace EFQueryLens.Core.Tests;

public class DaemonGrpcTransportTests
{
    [Fact]
    public async Task Subscribe_WhenTranslationStateChanges_StreamsStateChangedEvent()
    {
        var workspacePath = CreateWorkspacePath();
        Directory.CreateDirectory(workspacePath);

        try
        {
            var daemonAssemblyPath = ResolveDaemonAssemblyPath();
            var port = await StartDaemonAsync(workspacePath, daemonAssemblyPath, TimeSpan.FromSeconds(20));
            var contextName = $"sub-{Guid.NewGuid():N}";

            await using var engine = new DaemonBackedEngine("127.0.0.1", port, contextName);
            using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
            var grpcClient = new DaemonService.DaemonServiceClient(channel);

            using var subscribeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var subscribeCall = grpcClient.Subscribe(new SubscribeRequest(), cancellationToken: subscribeCts.Token);

            var waitEventTask = WaitForStateChangedEventAsync(
                subscribeCall.ResponseStream,
                contextName,
                TimeSpan.FromSeconds(10));

            var request = new TranslationRequest
            {
                AssemblyPath = Path.Combine(workspacePath, "missing-sample.dll"),
                Expression = "db.Users.Where(u => u.IsActive)",
            };

            _ = await WaitForReadyAsync(engine, request, TimeSpan.FromSeconds(10));
            var stateChanged = await waitEventTask;

            Assert.Equal(contextName, stateChanged.ContextName);
            Assert.True(
                stateChanged.State is DaemonWarmState.Warming or DaemonWarmState.Cold or DaemonWarmState.Ready,
                $"Unexpected state: {stateChanged.State}");
        }
        finally
        {
            TryKillDaemonFromPidFile(workspacePath);
            TryDeleteDirectory(workspacePath);
        }
    }

    [Fact]
    public async Task DaemonGrpc_StartPingTranslateQueued_Shutdown()
    {
        var workspacePath = CreateWorkspacePath();
        Directory.CreateDirectory(workspacePath);

        try
        {
            var daemonAssemblyPath = ResolveDaemonAssemblyPath();
            var port = await StartDaemonAsync(workspacePath, daemonAssemblyPath, TimeSpan.FromSeconds(20));

            await using var engine = new DaemonBackedEngine("127.0.0.1", port, contextName: $"test-{Environment.ProcessId}");

            using (var pingCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await engine.PingAsync(pingCts.Token);
            }

            var request = new TranslationRequest
            {
                AssemblyPath = Path.Combine(workspacePath, "missing-sample.dll"),
                Expression = "db.Users.Where(u => u.Id == 1)",
            };

            var ready = await WaitForReadyAsync(engine, request, TimeSpan.FromSeconds(10));

            Assert.NotNull(ready);
            Assert.Equal(QueryTranslationStatus.Ready, ready.Status);
            Assert.NotNull(ready.Result);
            Assert.False(ready.Result!.Success);

            using (var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await engine.ShutdownDaemonAsync(shutdownCts.Token);
            }

            await WaitForDaemonExitAsync(workspacePath, TimeSpan.FromSeconds(10));
        }
        finally
        {
            TryKillDaemonFromPidFile(workspacePath);
            TryDeleteDirectory(workspacePath);
        }
    }

    [Fact]
    public async Task ResiliencyEngine_RestartDaemon_ReconnectsAndTranslates()
    {
        var workspacePath = CreateWorkspacePath();
        Directory.CreateDirectory(workspacePath);

        try
        {
            var daemonAssemblyPath = ResolveDaemonAssemblyPath();
            var port = await StartDaemonAsync(workspacePath, daemonAssemblyPath, TimeSpan.FromSeconds(20));
            var contextName = $"test-{Guid.NewGuid():N}";

            await using var baseEngine = new DaemonBackedEngine("127.0.0.1", port, contextName);
            await using var resiliency = new ResiliencyDaemonEngine(
                baseEngine,
                workspacePath,
                daemonExecutablePath: null,
                daemonAssemblyPath: daemonAssemblyPath,
                contextName: contextName,
                connectTimeoutMs: 4000,
                startTimeoutMs: 15000,
                shutdownDaemonOnDispose: true,
                ownsDaemonLifecycle: true);

            var translationRequest = new TranslationRequest
            {
                AssemblyPath = Path.Combine(workspacePath, "missing-sample.dll"),
                Expression = "db.Users.Select(u => u.Id)",
            };

            var firstReady = await WaitForReadyAsync(resiliency, translationRequest, TimeSpan.FromSeconds(10));
            Assert.NotNull(firstReady.Result);
            Assert.False(firstReady.Result!.Success);

            using (var restartCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var restarted = await resiliency.RestartDaemonAsync(restartCts.Token);
                Assert.True(restarted);
            }

            var secondReady = await WaitForReadyAsync(resiliency, translationRequest, TimeSpan.FromSeconds(10));
            Assert.NotNull(secondReady.Result);
            Assert.False(secondReady.Result!.Success);
        }
        finally
        {
            TryKillDaemonFromPidFile(workspacePath);
            TryDeleteDirectory(workspacePath);
        }
    }

    private static string CreateWorkspacePath() =>
        Path.Combine(Path.GetTempPath(), "querylens-grpc-tests", Guid.NewGuid().ToString("N"));

    private static string ResolveDaemonAssemblyPath()
    {
        var fromLocator = DaemonLocator.ResolveDaemonAssemblyPath();
        if (!string.IsNullOrWhiteSpace(fromLocator) && File.Exists(fromLocator))
        {
            return fromLocator;
        }

        // Fall back to source build output when tests run from a different output layout.
        var candidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "EFQueryLens.Daemon", "bin", "Debug", "net10.0", "EFQueryLens.Daemon.dll"));

        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException("Could not locate EFQueryLens.Daemon.dll for transport tests.", candidate);
    }

    private static async Task<int> StartDaemonAsync(string workspacePath, string daemonAssemblyPath, TimeSpan timeout)
    {
        var port = await DaemonLocator.TryGetOrStartDaemonAsync(
            workspacePath,
            daemonExecutablePath: null,
            daemonAssemblyPath: daemonAssemblyPath,
            timeoutMilliseconds: (int)timeout.TotalMilliseconds,
            debugLog: null,
            ct: CancellationToken.None);

        Assert.True(port is > 0, $"Daemon did not start for workspace '{workspacePath}'.");
        return port!.Value;
    }

    private static async Task<QueuedTranslationResult> WaitForReadyAsync(
        IQueryLensEngine engine,
        TranslationRequest request,
        TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var queued = await engine.TranslateQueuedAsync(request, cts.Token);
            if (queued.Status == QueryTranslationStatus.Ready)
            {
                return queued;
            }

            await Task.Delay(120);
        }

        throw new TimeoutException("Timed out waiting for queued translation to become Ready.");
    }

    private static async Task WaitForDaemonExitAsync(string workspacePath, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (DaemonLocator.TryGetPort(workspacePath) is null)
            {
                return;
            }

            await Task.Delay(120);
        }
    }

    private static async Task<StateChangedEvent> WaitForStateChangedEventAsync(
        IAsyncStreamReader<DaemonEvent> stream,
        string contextName,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (await stream.MoveNext(cts.Token))
        {
            var daemonEvent = stream.Current;
            if (daemonEvent.EventCase is not DaemonEvent.EventOneofCase.StateChanged)
            {
                continue;
            }

            if (!string.Equals(daemonEvent.StateChanged.ContextName, contextName, StringComparison.Ordinal))
            {
                continue;
            }

            return daemonEvent.StateChanged;
        }

        throw new TimeoutException($"Timed out waiting for StateChanged event for context '{contextName}'.");
    }

    private static void TryKillDaemonFromPidFile(string workspacePath)
    {
        var pidFilePath = DaemonWorkspaceIdentity.BuildPidFilePath(workspacePath);
        if (!File.Exists(pidFilePath))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pidFilePath));
            if (!doc.RootElement.TryGetProperty("processId", out var processIdElement))
            {
                return;
            }

            var processId = processIdElement.GetInt32();
            if (processId <= 0)
            {
                return;
            }

            var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }

        try
        {
            File.Delete(pidFilePath);
        }
        catch
        {
            // Best effort cleanup only.
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
            // Best effort cleanup only.
        }
    }
}
