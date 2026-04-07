using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Engine;

namespace EFQueryLens.Core.Tests.Lsp.Fakes;

internal sealed class TestQueryLensEngine : IQueryLensEngine
{
    public bool ThrowOnGenerateFactory { get; set; }
    public int RestartCalls { get; private set; }
    public int InvalidateCalls { get; private set; }

    public Task<QueryTranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
        => Task.FromResult(new QueryTranslationResult
        {
            Success = false,
            Metadata = new TranslationMetadata
            {
                DbContextType = "MyDb",
                EfCoreVersion = "9.0.0",
                ProviderName = "Provider",
                TranslationTime = TimeSpan.FromMilliseconds(1),
            },
        });

    public Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
        => Task.FromResult(new ModelSnapshot { DbContextType = "MyDb" });

    public Task<FactoryGenerationResult> GenerateFactoryAsync(FactoryGenerationRequest request, CancellationToken ct = default)
    {
        if (ThrowOnGenerateFactory)
            throw new InvalidOperationException("factory failed");

        return Task.FromResult(new FactoryGenerationResult
        {
            Content = "// generated",
            SuggestedFileName = "Factory.cs",
            DbContextTypeFullName = request.DbContextTypeName ?? "MyDb",
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestControllableEngine : IQueryLensEngine, IEngineControl
{
    public bool ThrowOnRestart { get; set; }
    public bool ThrowOnInvalidate { get; set; }
    public TimeSpan InspectModelDelay { get; set; }
    public int RestartCalls { get; private set; }
    public int InvalidateCalls { get; private set; }

    public Task<QueryTranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
        => Task.FromResult(new QueryTranslationResult
        {
            Success = true,
            Sql = "SELECT 1",
            Metadata = new TranslationMetadata
            {
                DbContextType = "MyDb",
                EfCoreVersion = "9.0.0",
                ProviderName = "Provider",
                TranslationTime = TimeSpan.FromMilliseconds(1),
            },
        });

    public async Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
    {
        if (InspectModelDelay > TimeSpan.Zero)
            await Task.Delay(InspectModelDelay, ct);

        return new ModelSnapshot { DbContextType = "MyDb" };
    }

    public Task<FactoryGenerationResult> GenerateFactoryAsync(FactoryGenerationRequest request, CancellationToken ct = default)
        => Task.FromResult(new FactoryGenerationResult
        {
            Content = "// generated",
            SuggestedFileName = "Factory.cs",
            DbContextTypeFullName = request.DbContextTypeName ?? "MyDb",
        });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task RestartAsync(CancellationToken ct = default)
    {
        RestartCalls++;
        if (ThrowOnRestart)
            throw new InvalidOperationException("restart failed");
        return Task.CompletedTask;
    }

    public Task InvalidateCacheAsync(CancellationToken ct = default)
    {
        InvalidateCalls++;
        if (ThrowOnInvalidate)
            throw new InvalidOperationException("invalidate failed");
        return Task.CompletedTask;
    }

    public Task WarmTranslateAsync(TranslationRequest request, CancellationToken ct = default)
        => Task.CompletedTask;
}
