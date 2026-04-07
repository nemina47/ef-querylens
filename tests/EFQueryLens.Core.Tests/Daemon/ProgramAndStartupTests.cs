using System.Reflection;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Daemon;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EFQueryLens.Core.Tests.Daemon;

public class ProgramAndStartupTests
{
    [Fact]
    public async Task Main_WithInvalidArgs_ReturnsExitCodeOne()
    {
        var method = typeof(EFQueryLens.Daemon.Program)
            .GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Program.Main not found via reflection.");

        var task = (Task<int>)method.Invoke(null, [Array.Empty<string>()])!;
        var exitCode = await task;

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData(new[] { "--workspace", "./repo", "--port", "1234" }, true, "./repo", 1234)]
    [InlineData(new[] { "--workspace", "./repo", "--port", "99999" }, true, "./repo", null)]
    [InlineData(new[] { "--port", "1234" }, false, "", 1234)]
    public void TryParseArgs_ParsesWorkspaceAndPort(string[] args, bool expectedOk, string expectedWorkspace, int? expectedPort)
    {
        var method = typeof(EFQueryLens.Daemon.Program)
            .GetMethod("TryParseArgs", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Program.TryParseArgs not found via reflection.");

        object?[] invokeArgs = [args, null, null];
        var ok = (bool)method.Invoke(null, invokeArgs)!;

        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedWorkspace, (string)invokeArgs[1]!);
        Assert.Equal(expectedPort, (int?)invokeArgs[2]);
    }

    [Fact]
    public void ResolveBoundPort_WithRequestedPort_ReturnsRequestedValue()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        var port = DaemonStartup.ResolveBoundPort(app, 4321);

        Assert.Equal(4321, port);
    }

    [Fact]
    public void ResolveBoundPort_WithoutServerAddresses_Throws()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        Assert.Throws<InvalidOperationException>(() => DaemonStartup.ResolveBoundPort(app, null));
    }

    [Fact]
    public void AddDaemonServices_RegistersRuntimeAndEngine()
    {
        var builder = WebApplication.CreateBuilder();

        DaemonStartup.AddDaemonServices(builder, QueryLensJsonOptions.Create());

        var provider = builder.Services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<DaemonRuntime>());
        Assert.NotNull(provider.GetRequiredService<IQueryLensEngine>());
    }

    [Fact]
    public void QueryLensJsonOptions_TimeSpanConverters_RoundTripTicks()
    {
        var options = QueryLensJsonOptions.Create();
        var payload = new JsonPayload
        {
            Duration = TimeSpan.FromSeconds(5),
            OptionalDuration = TimeSpan.FromMilliseconds(250),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload, options);
        Assert.Contains("\"duration\":50000000", json, StringComparison.Ordinal);
        Assert.Contains("\"optionalDuration\":2500000", json, StringComparison.Ordinal);

        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<JsonPayload>(json, options);
        Assert.NotNull(roundTrip);
        Assert.Equal(payload.Duration, roundTrip.Duration);
        Assert.Equal(payload.OptionalDuration, roundTrip.OptionalDuration);
    }

    [Fact]
    public async Task IdleShutdownTimer_StopsApp_WhenIdleWindowExceeded()
    {
        var builder = WebApplication.CreateBuilder();
        DaemonStartup.ConfigureKestrel(builder, 0);
        var app = builder.Build();

        await app.StartAsync();

        var runtime = new DaemonRuntime(new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        var lastActivityField = typeof(DaemonRuntime)
            .GetField("_lastActivity", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _lastActivity via reflection.");
        lastActivityField.SetValue(runtime, DateTime.UtcNow - TimeSpan.FromMinutes(20));

        using var timer = (System.Timers.Timer)IdleShutdownTimer.Start(app, runtime, idleMinutes: 1);
        timer.Interval = 10;

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!app.Lifetime.ApplicationStopping.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(app.Lifetime.ApplicationStopping.IsCancellationRequested);

        await app.StopAsync();
    }

    private sealed class JsonPayload
    {
        public TimeSpan Duration { get; init; }
        public TimeSpan? OptionalDuration { get; init; }
    }
}
