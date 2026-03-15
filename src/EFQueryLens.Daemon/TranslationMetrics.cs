using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EFQueryLens.Daemon;

internal sealed class TranslationMetrics
{
    private sealed class RollingMetricState
    {
        public readonly object SyncRoot = new();
        public readonly Queue<long> Samples = new();
        public double SumMs;
        public int TotalSampleCount;
    }

    private readonly ConcurrentDictionary<string, RollingMetricState> _state = new(StringComparer.Ordinal);
    private readonly int _sampleWindowSize;
    private readonly int _warmThresholdMs;
    private readonly int _minSamplesBeforeReady;

    public TranslationMetrics()
        : this(
            sampleWindowSize: ReadIntEnvironmentVariable(
                "QUERYLENS_AVG_WINDOW_SAMPLES",
                fallback: 20,
                min: 1,
                max: 500),
            warmThresholdMs: ReadIntEnvironmentVariable(
                "QUERYLENS_WARM_THRESHOLD_MS",
                fallback: 1200,
                min: 100,
                max: 30_000),
            minSamplesBeforeReady: 3)
    {
    }

    internal TranslationMetrics(int sampleWindowSize, int warmThresholdMs, int minSamplesBeforeReady)
    {
        _sampleWindowSize = Math.Clamp(sampleWindowSize, 1, 500);
        _warmThresholdMs = Math.Clamp(warmThresholdMs, 100, 30_000);
        _minSamplesBeforeReady = Math.Clamp(minSamplesBeforeReady, 1, _sampleWindowSize);
    }

    public void RecordSample(string contextName, long elapsedMilliseconds)
    {
        var key = NormalizeContext(contextName);
        var sample = Math.Max(0, elapsedMilliseconds);
        var state = _state.GetOrAdd(key, static _ => new RollingMetricState());

        lock (state.SyncRoot)
        {
            state.TotalSampleCount++;
            state.Samples.Enqueue(sample);
            state.SumMs += sample;

            while (state.Samples.Count > _sampleWindowSize)
            {
                state.SumMs -= state.Samples.Dequeue();
            }
        }
    }

    public double GetAverageMs(string contextName)
    {
        var key = NormalizeContext(contextName);
        if (!_state.TryGetValue(key, out var state))
        {
            return 0;
        }

        lock (state.SyncRoot)
        {
            if (state.Samples.Count == 0)
            {
                return 0;
            }

            return state.SumMs / state.Samples.Count;
        }
    }

    public bool IsWarming(string contextName)
    {
        var key = NormalizeContext(contextName);
        if (!_state.TryGetValue(key, out var state))
        {
            return true;
        }

        lock (state.SyncRoot)
        {
            if (state.TotalSampleCount < _minSamplesBeforeReady)
            {
                return true;
            }

            if (state.Samples.Count == 0)
            {
                return true;
            }

            var averageMs = state.SumMs / state.Samples.Count;
            return averageMs > _warmThresholdMs;
        }
    }

    public int GetAdaptiveWaitMs(string contextName)
    {
        var avg = GetAverageMs(contextName);
        return avg > 0 && avg < 200 ? 200 : 0;
    }

    private static string NormalizeContext(string? contextName)
    {
        if (string.IsNullOrWhiteSpace(contextName))
        {
            return "default";
        }

        return contextName.Trim();
    }

    private static int ReadIntEnvironmentVariable(string variableName, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (!int.TryParse(raw, out var value))
        {
            return fallback;
        }

        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }
}
