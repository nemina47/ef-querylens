using EFQueryLens.Core.Tests.Lsp.Fakes;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Hosting;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;

namespace EFQueryLens.Core.Tests.Lsp;

public class LanguageServerHandlerTests
{
    [Fact]
    public void Initialize_ReflectsHoverProviderSetting()
    {
        var handler = CreateHandler();

        var response = handler.Initialize(JObject.Parse("""
            {
              "initializationOptions": {
                "queryLens": {
                  "enableLspHover": false
                }
              }
            }
            """));

        Assert.False(response["capabilities"]!["hoverProvider"]!.Value<bool>());
    }

    [Fact]
    public void DidChangeConfiguration_AppliesWithoutThrowing()
    {
        var handler = CreateHandler();

        handler.DidChangeConfiguration(JObject.Parse("""
            {
              "settings": {
                "queryLens": {
                  "debugEnabled": true,
                  "enableLspHover": true
                }
              }
            }
            """));
    }

    [Fact]
    public async Task ExecuteCommand_MissingCommand_ReturnsFailure()
    {
        var handler = CreateHandler();

        var result = await handler.ExecuteCommandAsync(new JObject(), CancellationToken.None);

        Assert.False(result!["success"]!.Value<bool>());
        Assert.Contains("Missing command", result["message"]!.Value<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteCommand_Unsupported_ReturnsFailure()
    {
        var handler = CreateHandler();

        var result = await handler.ExecuteCommandAsync(
            new JObject { ["command"] = "unknown.command" },
            CancellationToken.None);

        Assert.False(result!["success"]!.Value<bool>());
        Assert.Contains("Unsupported command", result["message"]!.Value<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteCommand_Warmup_MissingPayload_ReturnsFailure()
    {
        var handler = CreateHandler();

        var result = await handler.ExecuteCommandAsync(
            new JObject { ["command"] = "efquerylens.warmup", ["arguments"] = new JArray() },
            CancellationToken.None);

        Assert.False(result!["success"]!.Value<bool>());
    }

    [Fact]
    public async Task ExecuteCommand_Recalculate_MissingPayload_ReturnsFailure()
    {
        var handler = CreateHandler();

        var result = await handler.ExecuteCommandAsync(
            new JObject { ["command"] = "efquerylens.preview.recalculate", ["arguments"] = new JArray() },
            CancellationToken.None);

        Assert.False(result!["success"]!.Value<bool>());
    }

    [Fact]
    public async Task ExecuteCommand_StructuredHover_MissingPayload_ReturnsFailure()
    {
        var handler = CreateHandler();

        var result = await handler.ExecuteCommandAsync(
            new JObject { ["command"] = "efquerylens.preview.structuredHover", ["arguments"] = new JArray() },
            CancellationToken.None);

        Assert.False(result!["success"]!.Value<bool>());
    }

    [Fact]
    public async Task ExecuteCommand_DaemonRestart_UsesControl()
    {
        var engine = new TestControllableEngine();
        var handler = CreateHandler(engine);

        var result = await handler.ExecuteCommandAsync(
            new JObject { ["command"] = "efquerylens.daemon.restart" },
            CancellationToken.None);

        var successToken = result!["success"] ?? result["Success"];
        Assert.NotNull(successToken);
        Assert.True(successToken!.Value<bool>());
        Assert.Equal(1, engine.RestartCalls);
    }

    [Fact]
    public async Task GenerateFactoryCommand_MissingPayload_ReturnsFailure()
    {
        var handler = CreateHandler();

        var result = await handler.ExecuteCommandAsync(
            new JObject { ["command"] = "efquerylens.generatefactory", ["arguments"] = new JArray() },
            CancellationToken.None);

        Assert.False(result!["success"]!.Value<bool>());
    }

    [Fact]
    public void Exit_WithoutShutdown_SetsExitCodeOne()
    {
        var previous = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            var handler = CreateHandler();
            handler.Exit();

            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    [Fact]
    public void Exit_AfterShutdown_DoesNotForceExitCodeOne()
    {
        var previous = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            var handler = CreateHandler();
            handler.Shutdown();
            handler.Exit();

            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    private static LanguageServerHandler CreateHandler(TestControllableEngine? engine = null)
    {
        var effectiveEngine = engine ?? new TestControllableEngine();
        var docs = new DocumentManager();
        var hoverService = new HoverPreviewService(effectiveEngine);

        var hover = new HoverHandler(docs, hoverService);
        var warmup = new WarmupHandler(docs, effectiveEngine);
        var daemonControl = new DaemonControlHandler(effectiveEngine);
        var textSync = new TextDocumentSyncHandler(docs);
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
