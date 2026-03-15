using System.IO.Pipes;
using EFQueryLens.Core;
using StreamJsonRpc;

namespace EFQueryLens.DaemonClient;

/// <summary>
/// Wraps <see cref="DaemonBackedEngine"/> with transparent reconnection logic.
/// If the daemon transport fails, re-discover/restart daemon and retry once.
/// </summary>
public sealed class ResiliencyDaemonEngine : IQueryLensEngine, IAsyncDisposable
{
    private DaemonBackedEngine _inner;
    private readonly string _workspacePath;
    private readonly string? _daemonExecutablePath;
    private readonly string? _daemonAssemblyPath;
    private readonly string _contextName;
    private readonly int _connectTimeoutMs;
    private readonly int _startTimeoutMs;
    private readonly Action<string>? _debugLog;
    private readonly bool _shutdownDaemonOnDispose;
    private bool _ownsDaemonLifecycle;
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private volatile bool _disposed;

    public ResiliencyDaemonEngine(
        DaemonBackedEngine inner,
        string workspacePath,
        string? daemonExecutablePath,
        string? daemonAssemblyPath,
        string contextName,
        int connectTimeoutMs = 2500,
        int startTimeoutMs = 10000,
        bool shutdownDaemonOnDispose = false,
        bool ownsDaemonLifecycle = false,
        Action<string>? debugLog = null)
    {
        _inner = inner;
        _workspacePath = workspacePath;
        _daemonExecutablePath = daemonExecutablePath;
        _daemonAssemblyPath = daemonAssemblyPath;
        _contextName = contextName;
        _connectTimeoutMs = connectTimeoutMs;
        _startTimeoutMs = startTimeoutMs;
        _shutdownDaemonOnDispose = shutdownDaemonOnDispose;
        _ownsDaemonLifecycle = ownsDaemonLifecycle;
        _debugLog = debugLog;
    }

    public async Task<QueryTranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct = default) =>
        await ExecuteWithReconnectAsync(e => e.TranslateAsync(request, ct), ct);

    public Task<ExplainResult> ExplainAsync(
        ExplainRequest request, CancellationToken ct = default) =>
        _inner.ExplainAsync(request, ct);

    public async Task<ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request, CancellationToken ct = default) =>
        await ExecuteWithReconnectAsync(e => e.InspectModelAsync(request, ct), ct);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        if (_shutdownDaemonOnDispose && _ownsDaemonLifecycle)
        {
            _debugLog?.Invoke("daemon-shutdown-on-dispose begin");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _inner.ShutdownDaemonAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(2));
                _debugLog?.Invoke("daemon-shutdown-on-dispose completed");
            }
            catch (TimeoutException)
            {
                _debugLog?.Invoke("daemon-shutdown-on-dispose timeout");
            }
            catch (OperationCanceledException)
            {
                _debugLog?.Invoke("daemon-shutdown-on-dispose canceled");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"daemon-shutdown-on-dispose failed type={ex.GetType().Name} message={ex.Message}");
            }
        }

        await _inner.DisposeAsync();
        _reconnectGate.Dispose();
    }

    /// <summary>
    /// Requests daemon restart by shutting down current daemon and reconnecting.
    /// </summary>
    public async Task<bool> RestartDaemonAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _reconnectGate.WaitAsync(ct);
        try
        {
            _debugLog?.Invoke($"daemon-restart requested workspace={_workspacePath}");

            try
            {
                using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                shutdownCts.CancelAfter(TimeSpan.FromSeconds(2));
                await _inner.ShutdownDaemonAsync(shutdownCts.Token);
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"daemon-restart shutdown phase warning type={ex.GetType().Name} message={ex.Message}");
            }

            await Task.Delay(150, ct);
            await ReconnectCoreAsync(ct);
            _ownsDaemonLifecycle = true;
            _debugLog?.Invoke("daemon-restart success");
            return true;
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    private async Task<T> ExecuteWithReconnectAsync<T>(
        Func<DaemonBackedEngine, Task<T>> operation,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return await operation(_inner);
        }
        catch (Exception ex) when (IsDaemonTransportFailure(ex) && !ct.IsCancellationRequested)
        {
            _debugLog?.Invoke($"daemon-transport-failure will-reconnect type={ex.GetType().Name} message={ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _debugLog?.Invoke("daemon-connect-timeout will-reconnect");
        }

        await ReconnectAsync(ct);
        return await operation(_inner);
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        await _reconnectGate.WaitAsync(ct);
        try
        {
            await ReconnectCoreAsync(ct);
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    private async Task ReconnectCoreAsync(CancellationToken ct)
    {
        _debugLog?.Invoke($"daemon-reconnect workspace={_workspacePath}");

        var newPipeName = await DaemonLocator.TryGetOrStartDaemonAsync(
            _workspacePath,
            _daemonExecutablePath,
            _daemonAssemblyPath,
            timeoutMilliseconds: _startTimeoutMs,
            debugLog: _debugLog,
            ct: ct);

        if (string.IsNullOrWhiteSpace(newPipeName))
            throw new InvalidOperationException(
                $"QueryLens daemon unavailable for workspace '{_workspacePath}'.");

        _debugLog?.Invoke($"daemon-reconnect new-pipe={newPipeName}");

        var pipe = new NamedPipeClientStream(
            ".",
            newPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            using var timeoutCts = new CancellationTokenSource(_connectTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await pipe.ConnectAsync(linkedCts.Token);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }

        var oldEngine = _inner;
        _inner = new DaemonBackedEngine(pipe, _contextName);
        await oldEngine.DisposeAsync();

        _debugLog?.Invoke("daemon-reconnect success");
    }

    private static bool IsDaemonTransportFailure(Exception ex)
    {
        if (ex is ConnectionLostException
            || ex is IOException
            || ex is EndOfStreamException
            || ex is ObjectDisposedException)
        {
            return true;
        }

        if (ex is InvalidOperationException invalidOp
            && (invalidOp.Message.Contains("transport", StringComparison.OrdinalIgnoreCase)
                || invalidOp.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
                || invalidOp.InnerException is IOException))
        {
            return true;
        }

        return ex.InnerException is not null && IsDaemonTransportFailure(ex.InnerException);
    }
}
