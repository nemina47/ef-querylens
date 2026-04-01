using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Core.Tests.Lsp;

public class TextDocumentSyncHandlerTests
{
    [Fact]
    public void DidOpen_StoresDocumentText()
    {
        var manager = new DocumentManager();
        var handler = new TextDocumentSyncHandler(manager);
        var uri = new Uri("file:///c:/repo/file.cs");

        handler.DidOpen(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                Text = "var x = 1;",
            },
        });

        Assert.Equal("var x = 1;", manager.GetDocumentText(uri.ToString()));
    }

    [Fact]
    public void DidChange_WithNullChangeText_DoesNothing()
    {
        var manager = new DocumentManager();
        var handler = new TextDocumentSyncHandler(manager);
        var uri = new Uri("file:///c:/repo/file.cs");

        manager.UpdateDocument(uri.ToString(), "before");

        handler.DidChange(new DidChangeTextDocumentParams
        {
            TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = 1 },
            ContentChanges = [new TextDocumentContentChangeEvent()],
        });

        Assert.Equal("before", manager.GetDocumentText(uri.ToString()));
    }

    [Fact]
    public void DidChange_WithText_UpdatesDocument()
    {
        var manager = new DocumentManager();
        var handler = new TextDocumentSyncHandler(manager);
        var uri = new Uri("file:///c:/repo/file.cs");

        handler.DidChange(new DidChangeTextDocumentParams
        {
            TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = 1 },
            ContentChanges = [new TextDocumentContentChangeEvent { Text = "after" }],
        });

        Assert.Equal("after", manager.GetDocumentText(uri.ToString()));
    }

    [Fact]
    public void DidSave_WithNullText_DoesNothing()
    {
        var manager = new DocumentManager();
        var handler = new TextDocumentSyncHandler(manager);
        var uri = new Uri("file:///c:/repo/file.cs");

        manager.UpdateDocument(uri.ToString(), "before");

        handler.DidSave(new DidSaveTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Text = null,
        });

        Assert.Equal("before", manager.GetDocumentText(uri.ToString()));
    }

    [Fact]
    public void DidSave_WithText_UpdatesDocument_AndDidClose_RemovesIt()
    {
        var manager = new DocumentManager();
        var handler = new TextDocumentSyncHandler(manager);
        var uri = new Uri("file:///c:/repo/file.cs");

        handler.DidSave(new DidSaveTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Text = "saved",
        });

        Assert.Equal("saved", manager.GetDocumentText(uri.ToString()));

        handler.DidClose(new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
        });

        Assert.Null(manager.GetDocumentText(uri.ToString()));
    }
}
