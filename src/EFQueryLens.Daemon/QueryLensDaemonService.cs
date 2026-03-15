using System.Collections.Concurrent;
using System.Diagnostics;
using EFQueryLens.Core;
using EFQueryLens.Core.Daemon;
using StreamJsonRpc;

namespace EFQueryLens.Daemon;

/// <summary>
/// Server-side JSON-RPC handler registered with <see cref="JsonRpc"/> for each
/// daemon client connection. Methods are dispatched by StreamJsonRpc matching their
/// <see cref="JsonRpcMethodAttribute"/> wire names. One instance per connection.
/// </summary>
internal sealed class QueryLensDaemonService(
    IQueryLensEngine engine,
    SqlTranslationQueue queue,
    TranslationMetrics metrics,
    CancellationTokenSource shutdownCts,
    ConcurrentDictionary<string, DaemonWarmState> contextStates,
    DateTime startedUtc)
    : IDaemonService
{
    private readonly bool _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);

    [JsonRpcMethod(DaemonMethods.Translate)]
    public async Task<DaemonTranslateResponse> TranslateAsync(
        DaemonTranslateRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        LogDebug(
            $"translate-start context={request.ContextName} assembly={request.Request.AssemblyPath} " +
            $"exprLen={request.Request.Expression?.Length ?? 0}");

        TrackState(request.ContextName, DaemonWarmState.Warming);
        try
        {
            var result = await engine.TranslateAsync(request.Request, cancellationToken);
            sw.Stop();
            TrackState(request.ContextName, result.Success ? DaemonWarmState.Ready : DaemonWarmState.Cold);

            LogDebug(
                $"translate-finished context={request.ContextName} success={result.Success} " +
                $"elapsedMs={sw.ElapsedMilliseconds} commands={result.Commands.Count} sqlLen={(result.Sql?.Length ?? 0)}");

            return new DaemonTranslateResponse { Result = result };
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogDebug(
                $"translate-failed context={request.ContextName} elapsedMs={sw.ElapsedMilliseconds} " +
                $"type={ex.GetType().Name} message={ex.Message}");
            throw;
        }
    }

    [JsonRpcMethod(DaemonMethods.TranslateQueued)]
    public async Task<DaemonQueuedTranslateResponse> TranslateQueuedAsync(
        DaemonQueuedTranslateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var queued = await queue.EnqueueOrGetAsync(
                request.SemanticKey,
                request.ContextName,
                request.Request,
                cancellationToken);

            if (queued.Status is QueryTranslationStatus.Ready)
            {
                TrackState(request.ContextName, queued.Result?.Success == true
                    ? DaemonWarmState.Ready
                    : DaemonWarmState.Cold);
            }
            else
            {
                TrackState(request.ContextName, DaemonWarmState.Warming);
            }

            return new DaemonQueuedTranslateResponse
            {
                Status = queued.Status,
                JobId = queued.JobId,
                AverageTranslationMs = queued.AverageTranslationMs,
                Result = queued.Result,
            };
        }
        catch (Exception ex)
        {
            LogDebug(
                $"translate-queued-failed context={request.ContextName} " +
                $"type={ex.GetType().Name} message={ex.Message}");

            return new DaemonQueuedTranslateResponse
            {
                Status = QueryTranslationStatus.Unreachable,
                JobId = null,
                AverageTranslationMs = metrics.GetAverageMs(request.ContextName),
                Result = null,
            };
        }
    }

    [JsonRpcMethod(DaemonMethods.InspectModel)]
    public async Task<DaemonInspectResponse> InspectModelAsync(
        DaemonInspectRequest request, CancellationToken cancellationToken)
    {
        var result = await engine.InspectModelAsync(request.Request, cancellationToken);
        TrackState(request.ContextName, DaemonWarmState.Ready);
        return new DaemonInspectResponse { Result = result };
    }

    [JsonRpcMethod(DaemonMethods.GetState)]
    public Task<DaemonStateResponse> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var contexts = contextStates
            .Select(kvp => new DaemonContextState { ContextName = kvp.Key, State = kvp.Value })
            .OrderBy(s => s.ContextName, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(new DaemonStateResponse { Contexts = contexts });
    }

    [JsonRpcMethod(DaemonMethods.Ping)]
    public Task<DaemonPingResponse> PingAsync(CancellationToken cancellationToken = default)
    {
        var version = typeof(QueryLensDaemonService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var uptime = DateTime.UtcNow - startedUtc;
        return Task.FromResult(new DaemonPingResponse { Version = version, Uptime = uptime });
    }

    [JsonRpcMethod(DaemonMethods.Shutdown)]
    public Task<DaemonShutdownResponse> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        shutdownCts.Cancel();
        return Task.FromResult(new DaemonShutdownResponse());
    }

    private void TrackState(string contextName, DaemonWarmState state)
    {
        if (!string.IsNullOrWhiteSpace(contextName))
            contextStates[contextName] = state;
    }

    private static bool ReadBoolEnvironmentVariable(string variableName, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private void LogDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-DAEMON] {message}");
    }
}
