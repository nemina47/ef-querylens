using StreamJsonRpc;

namespace EFQueryLens.Core.Daemon;

/// <summary>
/// Contract for the daemon RPC service methods.
/// Shared as documentation and compile-time contract for both server and clients.
/// </summary>
public interface IDaemonService
{
    [JsonRpcMethod(DaemonMethods.Translate)]
    Task<DaemonTranslateResponse> TranslateAsync(DaemonTranslateRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(DaemonMethods.TranslateQueued)]
    Task<DaemonQueuedTranslateResponse> TranslateQueuedAsync(DaemonQueuedTranslateRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(DaemonMethods.InspectModel)]
    Task<DaemonInspectResponse> InspectModelAsync(DaemonInspectRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(DaemonMethods.GetState)]
    Task<DaemonStateResponse> GetStateAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(DaemonMethods.Ping)]
    Task<DaemonPingResponse> PingAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(DaemonMethods.Shutdown)]
    Task<DaemonShutdownResponse> ShutdownAsync(CancellationToken cancellationToken = default);
}
