using EFQueryLens.Core.Tests.Lsp.Fakes;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Hosting;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;

namespace EFQueryLens.Core.Tests.Lsp;

public class LanguageServerHandlerCommandsCoverageTests
{
    [Fact]
    public async Task WarmupAsync_WithMissingDocument_ReturnsEmptySource()
    {
        var handler = CreateHandler();

        var result = await handler.WarmupAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = new Uri("file:///c:/does-not-exist/file.cs") },
                Position = new Position(0, 0),
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("empty-source", result.Message);
    }

    [Fact]
    public async Task RestartDaemonAsync_UsesControlPath()
    {
        var engine = new TestControllableEngine();
        var handler = CreateHandler(engine);

        var result = await handler.RestartDaemonAsync(ct: CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, engine.RestartCalls);
    }

    [Fact]
    public async Task RecalculatePreviewAsync_WhenInvalidateFails_ReturnsFailurePayload()
    {
        var engine = new TestControllableEngine { ThrowOnInvalidate = true };
        var docs = new DocumentManager();
        var handler = CreateHandler(engine, docs);

        var result = await handler.RecalculatePreviewAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = new Uri("file:///c:/repo/file.cs") },
                Position = new Position(0, 0),
            },
            CancellationToken.None);

        Assert.False(result["success"]!.Value<bool>());
        Assert.Contains("failed", result["message"]!.Value<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateFactoryAsync_WithValidRequest_ReturnsSuccess()
    {
        var handler = CreateHandler();

        var result = await handler.GenerateFactoryAsync(
            new JObject
            {
                ["assemblyPath"] = "C:/app/app.dll",
                ["dbContextTypeName"] = "MyDb",
            },
            CancellationToken.None);

        Assert.True(result["success"]!.Value<bool>());
    }

    [Fact]
    public async Task ExecuteCommand_ShowOpenCopyReanalyze_WithValidPayload_ReturnSuccess()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var docs = new DocumentManager();
        var uri = new Uri(project.SourceFilePath);
        docs.UpdateDocument(uri.ToString(), project.QuerySource);

        var handler = CreateHandler(new TestControllableEngine(), docs);

        var payload = new JObject
        {
            ["command"] = "efquerylens.showsqlpopup",
            ["arguments"] = new JArray(PositionArg(uri, 11, 24)),
        };

        var show = await handler.ExecuteCommandAsync(payload, CancellationToken.None);
        Assert.True(show!["success"]!.Value<bool>());

        payload["command"] = "efquerylens.opensqleditor";
        var open = await handler.ExecuteCommandAsync(payload, CancellationToken.None);
        Assert.True(open!["success"]!.Value<bool>());

        payload["command"] = "efquerylens.copysql";
        var copy = await handler.ExecuteCommandAsync(payload, CancellationToken.None);
        Assert.True(copy!["success"]!.Value<bool>());

        payload["command"] = "efquerylens.reanalyze";
        var reanalyze = await handler.ExecuteCommandAsync(payload, CancellationToken.None);
        Assert.True(reanalyze!["success"]!.Value<bool>());
    }

    [Fact]
    public async Task ExecuteCommand_GenerateFactory_WithPayload_DelegatesToHandler()
    {
        var handler = CreateHandler();

        var result = await handler.ExecuteCommandAsync(
            new JObject
            {
                ["command"] = "efquerylens.generatefactory",
                ["arguments"] = new JArray(new JObject
                {
                    ["assemblyPath"] = "C:/app/app.dll",
                    ["dbContextTypeName"] = "MyDb",
                }),
            },
            CancellationToken.None);

        Assert.True(result!["success"]!.Value<bool>());
    }

    private static JObject PositionArg(Uri uri, int line, int character) =>
        new()
        {
            ["textDocument"] = new JObject { ["uri"] = uri.ToString() },
            ["position"] = new JObject { ["line"] = line, ["character"] = character },
        };

    private static LanguageServerHandler CreateHandler(TestControllableEngine? engine = null, DocumentManager? docs = null)
    {
        var effectiveEngine = engine ?? new TestControllableEngine();
        var documentManager = docs ?? new DocumentManager();
        var hoverService = new HoverPreviewService(effectiveEngine);

        var hover = new HoverHandler(documentManager, hoverService);
        var warmup = new WarmupHandler(documentManager, effectiveEngine);
        var daemonControl = new DaemonControlHandler(effectiveEngine);
        var textSync = new TextDocumentSyncHandler(documentManager);
        var generateFactory = new GenerateFactoryHandler(effectiveEngine);

        return new LanguageServerHandler(
            hover,
            warmup,
            daemonControl,
            textSync,
            generateFactory,
            debugEnabled: false);
    }
}
