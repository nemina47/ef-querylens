using EFQueryLens.Core.Tests.Lsp.Fakes;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Hosting;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Core.Tests.Lsp;

public class LanguageServerHandlerCodeLensTests
{
    [Fact]
    public void GetCodeLens_ReturnsEmpty_WhenDocumentNotTracked()
    {
        var handler = CreateHandler(new DocumentManager());

        var result = handler.GetCodeLens(new CodeLensParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri("file:///c:/repo/test.cs") },
        });

        Assert.Empty(result);
    }

    [Fact]
    public void GetCodeLens_ReturnsExpectedCommands_ForSingleLinqChain()
    {
        var docs = new DocumentManager();
        var uri = new Uri("file:///c:/repo/test.cs");
        docs.UpdateDocument(uri.ToString(), "var q = db.Orders.Where(o => o.Id > 0);");
        var handler = CreateHandler(docs);

        var result = handler.GetCodeLens(new CodeLensParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
        });

        Assert.Equal(4, result.Length);
        var commandIds = result.Select(r => r.Command?.CommandIdentifier).ToArray();
        Assert.Contains("efquerylens.showsqlpopup", commandIds);
        Assert.Contains("efquerylens.opensqleditor", commandIds);
        Assert.Contains("efquerylens.copysql", commandIds);
        Assert.Contains("efquerylens.reanalyze", commandIds);
    }

    private static LanguageServerHandler CreateHandler(DocumentManager docs)
    {
        var engine = new TestControllableEngine();
        var hoverService = new HoverPreviewService(engine);

        return new LanguageServerHandler(
            new HoverHandler(docs, hoverService),
            new WarmupHandler(docs, engine),
            new DaemonControlHandler(engine),
            new TextDocumentSyncHandler(docs),
            new GenerateFactoryHandler(engine),
            debugEnabled: false);
    }
}
