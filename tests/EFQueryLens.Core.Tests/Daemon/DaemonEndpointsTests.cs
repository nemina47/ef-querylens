using System.Net;
using System.Net.Http.Json;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Daemon;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace EFQueryLens.Core.Tests.Daemon;

public class DaemonEndpointsTests
{
    [Fact]
    public async Task Endpoints_BasicFlow_WorksAndUsesCache()
    {
        await using var appHandle = await TestDaemonApp.StartAsync();
        var client = appHandle.Client;

        var ping = await client.GetAsync("/ping");
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);

        var request = new TranslationRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "C:/app/MyApp.dll",
        };

        var firstTranslate = await client.PostAsJsonAsync("/translate", request);
        Assert.Equal(HttpStatusCode.OK, firstTranslate.StatusCode);

        var secondTranslate = await client.PostAsJsonAsync("/translate", request);
        Assert.Equal(HttpStatusCode.OK, secondTranslate.StatusCode);
        Assert.Equal(1, appHandle.Engine.TranslateCalls);

        var warm = await client.PostAsJsonAsync("/translate/warm", request);
        Assert.Equal(HttpStatusCode.Accepted, warm.StatusCode);

        var inspect = await client.PostAsJsonAsync(
            "/inspect-model",
            new ModelInspectionRequest { AssemblyPath = "C:/app/MyApp.dll" });
        Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);

        var model = await inspect.Content.ReadFromJsonAsync<ModelSnapshot>();
        Assert.NotNull(model);
        Assert.Equal("MyDb", model.DbContextType);

        var generateFactory = await client.PostAsJsonAsync(
            "/generate-factory",
            new FactoryGenerationRequest { AssemblyPath = "C:/app/MyApp.dll", DbContextTypeName = "MyDb" });
        Assert.Equal(HttpStatusCode.OK, generateFactory.StatusCode);

        var factory = await generateFactory.Content.ReadFromJsonAsync<FactoryGenerationResult>();
        Assert.NotNull(factory);
        Assert.Equal("MyDb", factory.DbContextTypeFullName);

        var invalidate = await client.PostAsync("/invalidate", null);
        Assert.Equal(HttpStatusCode.OK, invalidate.StatusCode);

        var thirdTranslate = await client.PostAsJsonAsync("/translate", request);
        Assert.Equal(HttpStatusCode.OK, thirdTranslate.StatusCode);
        Assert.Equal(2, appHandle.Engine.TranslateCalls);

        var shutdown = await client.PostAsync("/shutdown", null);
        Assert.Equal(HttpStatusCode.Accepted, shutdown.StatusCode);
    }

    [Fact]
    public async Task Translate_WhenEngineFails_DoesNotCacheFailure()
    {
        await using var appHandle = await TestDaemonApp.StartAsync();
        appHandle.Engine.ReturnSuccess = false;

        var request = new TranslationRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "C:/app/MyApp.dll",
        };

        var first = await appHandle.Client.PostAsJsonAsync("/translate", request);
        var second = await appHandle.Client.PostAsJsonAsync("/translate", request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, appHandle.Engine.TranslateCalls);
    }

    private sealed class TestDaemonApp : IAsyncDisposable
    {
        public required FakeEngine Engine { get; init; }
        public required HttpClient Client { get; init; }
        public required WebApplication App { get; init; }

        public static async Task<TestDaemonApp> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            DaemonStartup.ConfigureLogging(builder);
            DaemonStartup.ConfigureKestrel(builder, 0);
            builder.Services.AddMemoryCache();

            var app = builder.Build();
            var engine = new FakeEngine();
            var runtime = new DaemonRuntime(new MemoryCache(new MemoryCacheOptions()));

            DaemonEndpoints.Map(app, engine, runtime);

            await app.StartAsync();
            var port = DaemonStartup.ResolveBoundPort(app, null);

            return new TestDaemonApp
            {
                App = app,
                Engine = engine,
                Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") },
            };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            await Engine.DisposeAsync();
        }
    }

    private sealed class FakeEngine : IQueryLensEngine
    {
        private int _translateCalls;
        public int TranslateCalls => Volatile.Read(ref _translateCalls);
        public bool ReturnSuccess { get; set; } = true;

        public Task<QueryTranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _translateCalls);

            return Task.FromResult(new QueryTranslationResult
            {
                Success = ReturnSuccess,
                Sql = $"SELECT '{request.Expression}'",
                ErrorMessage = ReturnSuccess ? null : "boom",
                Metadata = new TranslationMetadata
                {
                    DbContextType = "MyDb",
                    EfCoreVersion = "9.0.0",
                    ProviderName = "Provider",
                    TranslationTime = TimeSpan.FromMilliseconds(1),
                },
            });
        }

        public Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new ModelSnapshot
            {
                DbContextType = "MyDb",
            });
        }

        public Task<FactoryGenerationResult> GenerateFactoryAsync(FactoryGenerationRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new FactoryGenerationResult
            {
                Content = "// generated",
                SuggestedFileName = "MyDbFactory.cs",
                DbContextTypeFullName = request.DbContextTypeName ?? "MyDb",
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
