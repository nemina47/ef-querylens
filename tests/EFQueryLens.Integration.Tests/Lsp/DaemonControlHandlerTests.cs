using EFQueryLens.Integration.Tests.Lsp.Fakes;
using EFQueryLens.Integration.Tests.Lsp.Fixtures;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;

namespace EFQueryLens.Integration.Tests.Lsp;

/// <summary>
/// Tests for <see cref="DaemonControlHandler"/> — no live engine connection required.
/// </summary>
public class DaemonControlHandlerTests : IClassFixture<LspTestFixture>
{
    private readonly LspTestFixture _fixture;

    public DaemonControlHandlerTests(LspTestFixture fixture)
    {
        _fixture = fixture;
    }

    // ── RestartAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RestartAsync_PlainEngine_ReturnsUnavailableResponse()
    {
        var engine = _fixture.CreatePlainEngine();
        var handler = new DaemonControlHandler(engine);

        var result = await handler.RestartAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestartAsync_EngineControl_ReturnsSuccessResponse()
    {
        var engine = _fixture.CreateControllableEngine();
        var handler = new DaemonControlHandler(engine);

        var result = await handler.RestartAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task RestartAsync_EngineControlThrows_ReturnsErrorResponse()
    {
        var engine = new FakeEngineControl { RestartException = new InvalidOperationException("boom") };
        var handler = new DaemonControlHandler(engine);

        var result = await handler.RestartAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("boom", result.Message, StringComparison.Ordinal);
    }

    // ── InvalidateQueryCachesAsync ───────────────────────────────────────────

    [Fact]
    public async Task InvalidateQueryCachesAsync_PlainEngine_ReturnsUnavailableResponse()
    {
        var engine = _fixture.CreatePlainEngine();
        var handler = new DaemonControlHandler(engine);

        var result = await handler.InvalidateQueryCachesAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidateQueryCachesAsync_EngineControl_ReturnsSuccessResponse()
    {
        var engine = _fixture.CreateControllableEngine();
        var handler = new DaemonControlHandler(engine);

        var result = await handler.InvalidateQueryCachesAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task InvalidateQueryCachesAsync_EngineControlThrows_ReturnsErrorResponse()
    {
        var engine = new FakeEngineControl
        {
            InvalidateException = new TimeoutException("cache timeout"),
        };
        var handler = new DaemonControlHandler(engine);

        var result = await handler.InvalidateQueryCachesAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cache timeout", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidateQueryCachesAsync_ReturnsZeroCounts()
    {
        var engine = _fixture.CreateControllableEngine();
        var handler = new DaemonControlHandler(engine);

        var result = await handler.InvalidateQueryCachesAsync(CancellationToken.None);

        Assert.Equal(0, result.RemovedCachedResults);
        Assert.Equal(0, result.RemovedInflightJobs);
    }

    // ── ApplyClientConfiguration ─────────────────────────────────────────────

    [Fact]
    public async Task ApplyClientConfiguration_DebugEnabled_DoesNotThrow()
    {
        var engine = _fixture.CreatePlainEngine();
        var handler = new DaemonControlHandler(engine);

        // Apply config with debug on — should toggle internal flag without throwing.
        handler.ApplyClientConfiguration(
            LspClientConfiguration.FromInitializeRequest(null) with { DebugEnabled = true });

        // Verify handler still operates — the log line will now fire to stderr, but no exception.
        var result = await handler.RestartAsync(CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public void ApplyClientConfiguration_NullDebugEnabled_DoesNotChangeState()
    {
        var engine = _fixture.CreateControllableEngine();
        var handler = new DaemonControlHandler(engine);

        // Should be a no-op for the debug flag when null.
        handler.ApplyClientConfiguration(
            LspClientConfiguration.FromInitializeRequest(null));
        handler.ApplyClientConfiguration(
            LspClientConfiguration.FromInitializeRequest(null) with { DebugEnabled = null });

        // No assertion beyond "did not throw."
    }
}
