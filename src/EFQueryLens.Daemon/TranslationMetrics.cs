using System.Collections.Concurrent;

namespace EFQueryLens.Daemon;

internal sealed class TranslationMetrics
{
    private sealed record MetricState(double AverageMs, int SampleCount);

    private readonly ConcurrentDictionary<string, MetricState> _state = new(StringComparer.Ordinal);
    private readonly double _alpha;
    private readonly int _warmThresholdMs;

    public TranslationMetrics()
    {
        _alpha = 0.15;
        _warmThresholdMs = ReadIntEnvironmentVariable(
            "QUERYLENS_WARM_THRESHOLD_MS",
            fallback: 1200,
            min: 100,
            max: 30_000);
    }

    public void RecordSample(string contextName, long elapsedMilliseconds)
    {
        var key = NormalizeContext(contextName);
        var sample = Math.Max(0, elapsedMilliseconds);

        _state.AddOrUpdate(
            key,
            _ => new MetricState(sample, 1),
            (_, existing) => new MetricState(
                AverageMs: existing.AverageMs <= 0
                    ? sample
                    : (_alpha * sample) + ((1 - _alpha) * existing.AverageMs),
                SampleCount: existing.SampleCount + 1));
    }

    public double GetAverageMs(string contextName)
    {
        var key = NormalizeContext(contextName);
        return _state.TryGetValue(key, out var metric)
            ? metric.AverageMs
            : 0;
    }

    public bool IsWarming(string contextName)
    {
        var key = NormalizeContext(contextName);
        if (!_state.TryGetValue(key, out var metric))
        {
            return true;
        }

        if (metric.SampleCount < 3)
        {
            return true;
        }

        return metric.AverageMs > _warmThresholdMs;
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
