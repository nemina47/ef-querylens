using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Engine;

namespace EFQueryLens.Integration.Tests.Lsp.Fakes;

/// <summary>
/// Implements <see cref="IQueryLensEngine"/> only — cannot be cast to <see cref="IEngineControl"/>.
/// </summary>
internal sealed class FakeQueryLensEngine : IQueryLensEngine
{
    public Func<TranslationRequest, QueryTranslationResult>? TranslateHandler { get; set; }

    public Task<QueryTranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(
            TranslateHandler?.Invoke(request)
            ?? new QueryTranslationResult { Success = false }
        );

    public Task<ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new ModelSnapshot { DbContextType = "FakeContext" });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Implements both <see cref="IQueryLensEngine"/> and <see cref="IEngineControl"/>.
/// Set <see cref="RestartException"/> or <see cref="InvalidateException"/> to inject faults.
/// </summary>
internal sealed class FakeEngineControl : IQueryLensEngine, IEngineControl
{
    public Exception? RestartException { get; set; }
    public Exception? InvalidateException { get; set; }

    public Task<QueryTranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new QueryTranslationResult { Success = false });

    public Task<ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new ModelSnapshot { DbContextType = "FakeContext" });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task RestartAsync(CancellationToken ct = default)
    {
        if (RestartException is not null)
        {
            throw RestartException;
        }

        return Task.CompletedTask;
    }

    public Task InvalidateCacheAsync(CancellationToken ct = default)
    {
        if (InvalidateException is not null)
        {
            throw InvalidateException;
        }

        return Task.CompletedTask;
    }

    public Task WarmTranslateAsync(TranslationRequest request, CancellationToken ct = default) =>
        Task.CompletedTask;
}
