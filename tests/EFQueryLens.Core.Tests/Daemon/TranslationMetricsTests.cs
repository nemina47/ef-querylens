namespace EFQueryLens.Core.Tests.Daemon;

using EFQueryLens.Daemon;

public sealed class TranslationMetricsTests
{
    [Fact]
    public void GetAverageMs_UsesRollingLastNSamples()
    {
        var metrics = new TranslationMetrics(sampleWindowSize: 3, warmThresholdMs: 1_200, minSamplesBeforeReady: 3);

        metrics.RecordSample("ctx", 100);
        metrics.RecordSample("ctx", 200);
        metrics.RecordSample("ctx", 300);
        Assert.Equal(200, metrics.GetAverageMs("ctx"), 1);

        metrics.RecordSample("ctx", 400);

        // Window now contains [200, 300, 400].
        Assert.Equal(300, metrics.GetAverageMs("ctx"), 1);
    }

    [Fact]
    public void IsWarming_UsesRollingAverageAfterMinimumSamples()
    {
        var metrics = new TranslationMetrics(sampleWindowSize: 3, warmThresholdMs: 250, minSamplesBeforeReady: 3);

        metrics.RecordSample("ctx", 500);
        metrics.RecordSample("ctx", 500);
        metrics.RecordSample("ctx", 500);
        Assert.True(metrics.IsWarming("ctx"));

        metrics.RecordSample("ctx", 50);
        metrics.RecordSample("ctx", 50);
        metrics.RecordSample("ctx", 50);

        // Window now contains [50, 50, 50], average is below threshold.
        Assert.False(metrics.IsWarming("ctx"));
    }

    [Fact]
    public void GetAdaptiveWaitMs_UsesRollingAverage()
    {
        var metrics = new TranslationMetrics(sampleWindowSize: 3, warmThresholdMs: 1_200, minSamplesBeforeReady: 3);

        metrics.RecordSample("ctx", 100);
        metrics.RecordSample("ctx", 120);
        metrics.RecordSample("ctx", 140);
        Assert.Equal(200, metrics.GetAdaptiveWaitMs("ctx"));

        metrics.RecordSample("ctx", 1_000);
        metrics.RecordSample("ctx", 1_000);
        metrics.RecordSample("ctx", 1_000);

        Assert.Equal(0, metrics.GetAdaptiveWaitMs("ctx"));
    }
}
