using System.Diagnostics;
using EFQueryLens.Core;
using EFQueryLens.Core.Grpc;
using Grpc.Net.Client;

namespace EFQueryLens.DaemonClient;

/// <summary>
/// <see cref="IQueryLensEngine"/> backed by a QueryLens daemon over gRPC loopback.
/// </summary>
public sealed partial class DaemonBackedEngine : IQueryLensEngine, IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly DaemonService.DaemonServiceClient _client;
    private readonly string _contextName;
    private readonly bool _debugEnabled;
    private readonly string _endpoint;

    /// <summary>
    /// Creates a new <see cref="DaemonBackedEngine"/> connected to the daemon gRPC endpoint.
    /// </summary>
    public DaemonBackedEngine(string host, int port, string contextName = "default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);

        _contextName = contextName;
        _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);
        _endpoint = $"http://{host}:{port}";
        _channel = GrpcChannel.ForAddress(_endpoint);
        _client = new DaemonService.DaemonServiceClient(_channel);
    }

    public async Task<QueryTranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct = default)
    {
        var payload = new TranslateRequest
        {
            ContextName = _contextName,
            Request = request.ToProto(),
        };

        var sw = Stopwatch.StartNew();
        LogDebug(
            $"translate-grpc-start endpoint={_endpoint} context={_contextName} assembly={request.AssemblyPath} " +
            $"exprLen={request.Expression?.Length ?? 0}");

        try
        {
            var response = await _client.TranslateAsync(payload, cancellationToken: ct).ResponseAsync;
            var result = response.Result.ToDomain();
            sw.Stop();
            LogDebug(
                $"translate-grpc-finished endpoint={_endpoint} context={_contextName} success={result.Success} " +
                $"elapsedMs={sw.ElapsedMilliseconds} commands={response.Result.Commands.Count} " +
                $"sqlLen={(result.Sql?.Length ?? 0)}");
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogDebug(
                $"translate-grpc-failed endpoint={_endpoint} context={_contextName} elapsedMs={sw.ElapsedMilliseconds} " +
                $"type={ex.GetType().Name} message={ex.Message}");
            throw;
        }
    }

    public async Task<QueuedTranslationResult> TranslateQueuedAsync(
        TranslationRequest request,
        CancellationToken ct = default)
    {
        var payload = new QueuedTranslateRequest
        {
            ContextName = _contextName,
            SemanticKey = BuildSemanticKey(request),
            Request = request.ToProto(),
        };

        var response = await _client.TranslateQueuedAsync(payload, cancellationToken: ct).ResponseAsync;
        return new QueuedTranslationResult
        {
            Status = response.Status.ToDomain(),
            JobId = response.HasJobId ? response.JobId : null,
            AverageTranslationMs = response.AverageTranslationMs,
            Result = response.Result is null ? null : response.Result.ToDomain(),
        };
    }

    public Task<ExplainResult> ExplainAsync(
        ExplainRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("ExplainAsync is not yet exposed by the daemon protocol.");

    public async Task<EFQueryLens.Core.ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request, CancellationToken ct = default)
    {
        var payload = new InspectModelRequest
        {
            ContextName = _contextName,
            AssemblyPath = request.AssemblyPath,
        };

        if (!string.IsNullOrWhiteSpace(request.DbContextTypeName))
        {
            payload.DbContextTypeName = request.DbContextTypeName;
        }

        var response = await _client.InspectModelAsync(payload, cancellationToken: ct).ResponseAsync;
        return response.Result.ToDomain();
    }

    /// <summary>
    /// Requests graceful daemon shutdown over RPC.
    /// </summary>
    public async Task ShutdownDaemonAsync(CancellationToken ct = default)
    {
        await _client.ShutdownAsync(new ShutdownRequest(), cancellationToken: ct).ResponseAsync;
    }

    public async Task PingAsync(CancellationToken ct = default)
    {
        await _client.PingAsync(new PingRequest(), cancellationToken: ct).ResponseAsync;
    }

    public async Task<InvalidateCacheResponse> InvalidateQueryCachesAsync(CancellationToken ct = default)
    {
        var response = await _client.InvalidateCacheAsync(new InvalidateCacheRequest
        {
            ContextName = _contextName,
            Scope = CacheInvalidationScope.QueryResults,
        }, cancellationToken: ct).ResponseAsync;

        return response;
    }

    public async Task SubscribeAsync(Action<DaemonEvent> onEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        using var call = _client.Subscribe(new SubscribeRequest(), cancellationToken: ct);
        while (await call.ResponseStream.MoveNext(ct))
        {
            var daemonEvent = call.ResponseStream.Current;
            if (daemonEvent is null)
            {
                continue;
            }

            onEvent(daemonEvent);
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }

}
