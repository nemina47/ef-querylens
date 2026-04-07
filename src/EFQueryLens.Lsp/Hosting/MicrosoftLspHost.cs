using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Services;
using StreamJsonRpc;

namespace EFQueryLens.Lsp.Hosting;

internal static class MicrosoftLspHost
{
    public static async Task RunAsync(IQueryLensEngine engine)
    {
        var debugEnabled = LspEnvironment.ReadBool("QUERYLENS_DEBUG", fallback: false);
        if (debugEnabled)
            Console.Error.WriteLine("[QL-LSP] host-run debug=true");

        var documentManager = new DocumentManager();
        var warmupHandler = new WarmupHandler(documentManager, engine);
        var hoverPreviewService = new HoverPreviewService(engine, warmupHandler, debugEnabled);
        var hoverHandler = new HoverHandler(documentManager, hoverPreviewService);
        var prewarm = new TranslationPrewarmService(hoverPreviewService, hoverHandler.StorePrewarmedEntry);

        var lspHandler = new LanguageServerHandler(
            hover: hoverHandler,
            warmup: warmupHandler,
            daemonControl: new DaemonControlHandler(engine),
            textSync: new TextDocumentSyncHandler(documentManager, prewarm),
            generateFactory: new GenerateFactoryHandler(engine),
            debugEnabled: debugEnabled);

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        var formatter = new JsonMessageFormatter();
        var msgHandler = new HeaderDelimitedMessageHandler(stdout, stdin, formatter);
        var rpc = new JsonRpc(msgHandler, lspHandler);
        lspHandler.JsonRpc = rpc;

        rpc.StartListening();
        if (debugEnabled)
            Console.Error.WriteLine("[QL-LSP] listening");

        await rpc.Completion;
    }
}
