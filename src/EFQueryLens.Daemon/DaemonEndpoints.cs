using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Engine;

namespace EFQueryLens.Daemon;

internal static class DaemonEndpoints
{
    internal static void Map(WebApplication app, IQueryLensEngine engine, DaemonRuntime runtime)
    {
        // GET /ping
        app.MapGet("/ping", () =>
        {
            runtime.Touch();
            var (hits, misses) = runtime.ReadStats();
            Console.Error.WriteLine($"[QL-Engine] ping cacheHits={hits} cacheMisses={misses}");
            return Results.Ok("pong");
        });

        // POST /translate
        app.MapPost("/translate", async (TranslationRequest request) =>
        {
            runtime.Touch();
            DaemonRuntime.ValidateSnapshotConsistency(request);

            var cacheKey = DaemonRuntime.ComputeCacheKey(request);

            if (runtime.TryGetCached(cacheKey, out var cached))
                return Results.Ok(cached);

            var lazy = runtime.GetOrAddInflight(
                cacheKey,
                _ => new Lazy<Task<QueryTranslationResult>>(
                    () => engine.TranslateAsync(request, CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            QueryTranslationResult result;
            try
            {
                result = await lazy.Value;
            }
            finally
            {
                runtime.RemoveInflight(cacheKey);
            }

            if (result.Success)
                runtime.SetCached(cacheKey, result);

            runtime.Touch();
            return Results.Ok(result);
        });

        // POST /translate/warm
        // Returns 202 immediately; starts a background translation so hover can hit cache.
        // Uses the same inflight dict as /translate — deduplicates concurrent requests naturally.
        app.MapPost("/translate/warm", (TranslationRequest request) =>
        {
            runtime.Touch();
            DaemonRuntime.ValidateSnapshotConsistency(request);
            var cacheKey = DaemonRuntime.ComputeCacheKey(request);

            if (runtime.IsCached(cacheKey))
                return Results.Accepted();

            runtime.GetOrAddInflight(
                cacheKey,
                key => new Lazy<Task<QueryTranslationResult>>(
                    () => Task.Run(async () =>
                    {
                        var r = await engine.TranslateAsync(request, CancellationToken.None);
                        if (r.Success)
                            runtime.SetCached(key, r);
                        runtime.RemoveInflight(key);
                        return r;
                    }),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return Results.Accepted();
        });

        // POST /inspect-model
        app.MapPost("/inspect-model", async (ModelInspectionRequest request) =>
        {
            runtime.Touch();
            var snapshot = await engine.InspectModelAsync(request, CancellationToken.None);
            runtime.Touch();
            return Results.Ok(snapshot);
        });

        // POST /generate-factory
        // Returns generated QueryLensDbContextFactory source and suggested file name.
        app.MapPost("/generate-factory", async (FactoryGenerationRequest request) =>
        {
            runtime.Touch();
            var result = await engine.GenerateFactoryAsync(request, CancellationToken.None);
            runtime.Touch();
            return Results.Ok(result);
        });

        // POST /invalidate
        app.MapPost("/invalidate", () =>
        {
            runtime.Touch();
            runtime.ClearCache();
            return Results.Ok();
        });

        // POST /shutdown
        app.MapPost("/shutdown", async () =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                await app.StopAsync();
            });
            return Results.Accepted();
        });
    }
}
