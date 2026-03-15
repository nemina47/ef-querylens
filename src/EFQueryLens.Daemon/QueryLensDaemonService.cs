using System.Collections.Concurrent;
using System.Diagnostics;
using EFQueryLens.Core;
using EFQueryLens.Core.Grpc;
using Grpc.Core;
using Microsoft.Extensions.Hosting;

namespace EFQueryLens.Daemon;

internal sealed class QueryLensDaemonService(
    IQueryLensEngine engine,
    SqlTranslationQueue queue,
    TranslationMetrics metrics,
    ConcurrentDictionary<string, DaemonWarmState> contextStates,
    DaemonEventStreamBroker eventStreamBroker,
    IHostApplicationLifetime hostLifetime)
    : DaemonService.DaemonServiceBase
{
    private readonly bool _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public override async Task<TranslateResponse> Translate(TranslateRequest request, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();
        var domainRequest = request.Request.ToDomain();

        LogDebug(
            $"translate-start context={request.ContextName} assembly={domainRequest.AssemblyPath} " +
            $"exprLen={domainRequest.Expression?.Length ?? 0}");

        TrackState(request.ContextName, DaemonWarmState.Warming);
        try
        {
            var result = await engine.TranslateAsync(domainRequest, context.CancellationToken);
            sw.Stop();

            TrackState(
                request.ContextName,
                result.Success ? DaemonWarmState.Ready : DaemonWarmState.Cold);

            LogDebug(
                $"translate-finished context={request.ContextName} success={result.Success} " +
                $"elapsedMs={sw.ElapsedMilliseconds} commands={result.Commands.Count} sqlLen={(result.Sql?.Length ?? 0)}");

            return new TranslateResponse
            {
                Result = result.ToProto(),
            };
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

    public override async Task<QueuedTranslateResponse> TranslateQueued(
        QueuedTranslateRequest request,
        ServerCallContext context)
    {
        try
        {
            var queued = await queue.EnqueueOrGetAsync(
                request.SemanticKey,
                request.ContextName,
                request.Request.ToDomain(),
                context.CancellationToken);

            if (queued.Status is QueryTranslationStatus.Ready)
            {
                TrackState(
                    request.ContextName,
                    queued.Result?.Success == true
                        ? DaemonWarmState.Ready
                        : DaemonWarmState.Cold);
            }
            else
            {
                TrackState(request.ContextName, DaemonWarmState.Warming);
            }

            var response = new QueuedTranslateResponse
            {
                Status = queued.Status.ToProto(),
                AverageTranslationMs = queued.AverageTranslationMs,
            };

            if (!string.IsNullOrWhiteSpace(queued.JobId))
            {
                response.JobId = queued.JobId;
            }

            if (queued.Result is not null)
            {
                response.Result = queued.Result.ToProto();
            }

            return response;
        }
        catch (Exception ex)
        {
            LogDebug(
                $"translate-queued-failed context={request.ContextName} " +
                $"type={ex.GetType().Name} message={ex.Message}");

            return new QueuedTranslateResponse
            {
                Status = TranslationStatus.Unreachable,
                AverageTranslationMs = metrics.GetAverageMs(request.ContextName),
            };
        }
    }

    public override async Task<InspectModelResponse> InspectModel(
        InspectModelRequest request,
        ServerCallContext context)
    {
        var domainRequest = new ModelInspectionRequest
        {
            AssemblyPath = request.AssemblyPath,
            DbContextTypeName = request.HasDbContextTypeName ? request.DbContextTypeName : null,
        };

        var snapshot = await engine.InspectModelAsync(domainRequest, context.CancellationToken);
        TrackState(request.ContextName, DaemonWarmState.Ready);

        return new InspectModelResponse
        {
            Result = snapshot.ToProto(),
        };
    }

    public override Task<GetStateResponse> GetState(GetStateRequest request, ServerCallContext context)
    {
        var response = new GetStateResponse();
        var contexts = contextStates
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new ContextStateEntry
            {
                ContextName = kvp.Key,
                State = kvp.Value,
            });
        response.Contexts.AddRange(contexts);
        return Task.FromResult(response);
    }

    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
    {
        var version = typeof(QueryLensDaemonService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var uptime = DateTime.UtcNow - _startedUtc;
        return Task.FromResult(new PingResponse
        {
            Version = version,
            UptimeMs = (long)Math.Max(0, uptime.TotalMilliseconds),
        });
    }

    public override Task<ShutdownResponse> Shutdown(ShutdownRequest request, ServerCallContext context)
    {
        hostLifetime.StopApplication();
        return Task.FromResult(new ShutdownResponse());
    }

    public override async Task Subscribe(
        SubscribeRequest request,
        IServerStreamWriter<DaemonEvent> responseStream,
        ServerCallContext context)
    {
        var subscription = eventStreamBroker.Subscribe(context.CancellationToken);

        try
        {
            // Send current known states first so new subscribers receive immediate context.
            foreach (var entry in contextStates.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                await responseStream.WriteAsync(new DaemonEvent
                {
                    StateChanged = new StateChangedEvent
                    {
                        ContextName = entry.Key,
                        State = entry.Value,
                    },
                });
            }

            await foreach (var daemonEvent in subscription.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(daemonEvent);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private void TrackState(string contextName, DaemonWarmState state)
    {
        if (string.IsNullOrWhiteSpace(contextName))
        {
            return;
        }

        if (contextStates.TryGetValue(contextName, out var existing) && existing == state)
        {
            return;
        }

        contextStates[contextName] = state;
        eventStreamBroker.PublishStateChanged(contextName, state);
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
