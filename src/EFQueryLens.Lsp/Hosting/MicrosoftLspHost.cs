using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Engine;
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

        var engineControl = engine as IEngineControl;
        var prewarm = engineControl is not null ? new TranslationPrewarmService(engineControl) : null;

        var documentManager = new DocumentManager();
        var hoverHandler = new HoverHandler(documentManager, new HoverPreviewService(engine, debugEnabled));
        var lspHandler = new LanguageServerHandler(
            hover: hoverHandler,
            warmup: new WarmupHandler(documentManager, engine),
            daemonControl: new DaemonControlHandler(engine),
            textSync: new TextDocumentSyncHandler(documentManager, prewarm),
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
