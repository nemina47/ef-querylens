using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Engine;

namespace EFQueryLens.Lsp.Handlers;

internal sealed record DaemonRestartResponse(bool Success, string Message);
internal sealed record DaemonCacheInvalidateResponse(bool Success, string Message, int RemovedCachedResults, int RemovedInflightJobs);

internal sealed class DaemonControlHandler
{
    private readonly IQueryLensEngine _engine;
    private bool _debugEnabled;

    public DaemonControlHandler(IQueryLensEngine engine)
    {
        _engine = engine;
        _debugEnabled = LspEnvironment.ReadBool("QUERYLENS_DEBUG", fallback: false);
    }

    public async Task<DaemonRestartResponse> RestartAsync(CancellationToken cancellationToken)
    {
        if (_engine is not IEngineControl control)
        {
            return new DaemonRestartResponse(false, "Engine restart is unavailable for this engine mode.");
        }

        try
        {
            await control.RestartAsync(cancellationToken);
            LogDebug("engine-restart-request success");
            return new DaemonRestartResponse(true, "Engine restarted.");
        }
        catch (Exception ex)
        {
            LogDebug($"engine-restart-request failed type={ex.GetType().Name} message={ex.Message}");
            return new DaemonRestartResponse(false, $"Engine restart failed: {ex.Message}");
        }
    }

    public async Task<DaemonCacheInvalidateResponse> InvalidateQueryCachesAsync(CancellationToken cancellationToken)
    {
        if (_engine is not IEngineControl control)
        {
            return new DaemonCacheInvalidateResponse(
                false,
                "Engine cache invalidation is unavailable for this engine mode.",
                0,
                0);
        }

        try
        {
            await control.InvalidateCacheAsync(cancellationToken);
            LogDebug("engine-cache-invalidate success");
            return new DaemonCacheInvalidateResponse(true, "Engine cache invalidated.", 0, 0);
        }
        catch (Exception ex)
        {
            LogDebug($"engine-cache-invalidate failed type={ex.GetType().Name} message={ex.Message}");
            return new DaemonCacheInvalidateResponse(false, $"Engine cache invalidation failed: {ex.Message}", 0, 0);
        }
    }

    public void ApplyClientConfiguration(LspClientConfiguration configuration)
    {
        if (configuration.DebugEnabled.HasValue)
        {
            _debugEnabled = configuration.DebugEnabled.Value;
        }
    }

    private void LogDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-DaemonCtl] {message}");
    }
}
