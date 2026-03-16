using System.Diagnostics;
using EFQueryLens.Core;

namespace EFQueryLens.Daemon;

internal sealed partial class SqlTranslationQueue
{
    public async Task<QueuedTranslationResult> EnqueueOrGetAsync(
        string semanticKey,
        string contextName,
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);
        ArgumentNullException.ThrowIfNull(request);

        SweepExpiredCacheIfNeeded();

        if (TryGetCachedReady(semanticKey, out var cached))
        {
            LogDebug($"queue-cache-hit context={contextName} semanticKeyLen={semanticKey.Length}");
            return new QueuedTranslationResult
            {
                Status = QueryTranslationStatus.Ready,
                JobId = cached!.JobId,
                AverageTranslationMs = _metrics.GetAverageMs(contextName),
                Result = cached.Result,
            };
        }

        if (_inflightJobs.TryGetValue(semanticKey, out var existingJobId))
        {
            var existingStatus = _metrics.IsWarming(contextName)
                ? QueryTranslationStatus.Starting
                : QueryTranslationStatus.InQueue;
            LogDebug($"queue-inflight-hit context={contextName} semanticKeyLen={semanticKey.Length} status={existingStatus}");
            return new QueuedTranslationResult
            {
                Status = existingStatus,
                JobId = existingJobId,
                AverageTranslationMs = _metrics.GetAverageMs(contextName),
            };
        }

        var jobId = Guid.NewGuid().ToString("N");
        if (!_inflightJobs.TryAdd(semanticKey, jobId))
        {
            if (_inflightJobs.TryGetValue(semanticKey, out var racingJobId))
            {
                var raceStatus = _metrics.IsWarming(contextName)
                    ? QueryTranslationStatus.Starting
                    : QueryTranslationStatus.InQueue;
                return new QueuedTranslationResult
                {
                    Status = raceStatus,
                    JobId = racingJobId,
                    AverageTranslationMs = _metrics.GetAverageMs(contextName),
                };
            }

            if (TryGetCachedReady(semanticKey, out var racedCached))
            {
                return new QueuedTranslationResult
                {
                    Status = QueryTranslationStatus.Ready,
                    JobId = racedCached!.JobId,
                    AverageTranslationMs = _metrics.GetAverageMs(contextName),
                    Result = racedCached.Result,
                };
            }

            var finalRaceStatus = _metrics.IsWarming(contextName)
                ? QueryTranslationStatus.Starting
                : QueryTranslationStatus.InQueue;
            return new QueuedTranslationResult
            {
                Status = finalRaceStatus,
                JobId = null,
                AverageTranslationMs = _metrics.GetAverageMs(contextName),
            };
        }

        try
        {
            var workItem = new TranslationWorkItem(
                semanticKey,
                contextName,
                request,
                jobId,
                Interlocked.Read(ref _cacheEpoch));
            await _channel.Writer.WriteAsync(workItem, cancellationToken);

            var status = _metrics.IsWarming(contextName)
                ? QueryTranslationStatus.Starting
                : QueryTranslationStatus.InQueue;
            LogDebug($"queue-enqueue context={contextName} semanticKeyLen={semanticKey.Length} status={status}");

            return new QueuedTranslationResult
            {
                Status = status,
                JobId = jobId,
                AverageTranslationMs = _metrics.GetAverageMs(contextName),
            };
        }
        catch
        {
            _inflightJobs.TryRemove(semanticKey, out _);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var workItem in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            var sw = Stopwatch.StartNew();
            QueryTranslationResult result;

            try
            {
                result = await _engine.TranslateAsync(workItem.Request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                result = BuildFailureResult($"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                sw.Stop();
            }

            _metrics.RecordSample(workItem.ContextName, sw.ElapsedMilliseconds);

            var currentEpoch = Interlocked.Read(ref _cacheEpoch);
            if (workItem.Epoch == currentEpoch)
            {
                _resultCache[workItem.SemanticKey] = new CachedTranslation(
                    DateTime.UtcNow.Ticks,
                    workItem.JobId,
                    result);
            }

            _inflightJobs.TryRemove(workItem.SemanticKey, out _);

            LogDebug(
                $"queue-complete context={workItem.ContextName} semanticKeyLen={workItem.SemanticKey.Length} " +
                $"elapsedMs={sw.ElapsedMilliseconds} success={result.Success}");
        }
    }
}
