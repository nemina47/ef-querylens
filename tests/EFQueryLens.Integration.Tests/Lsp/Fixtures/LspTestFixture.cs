using EFQueryLens.Integration.Tests.Lsp.Fakes;
using EFQueryLens.Lsp.Services;

namespace EFQueryLens.Integration.Tests.Lsp.Fixtures;

/// <summary>
/// Shared fixture for LSP test classes to centralize fake engine/service creation.
/// </summary>
public sealed class LspTestFixture
{
    internal FakeQueryLensEngine CreatePlainEngine() => new();

    internal FakeEngineControl CreateControllableEngine() => new();

    internal HoverPreviewService CreateHoverService(bool debugEnabled = false) =>
        new(CreatePlainEngine(), debugEnabled);
}
