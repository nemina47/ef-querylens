using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed class TextDocumentSyncHandler
{
    public DocumentManager DocumentManager { get; }
    private readonly TranslationPrewarmService? _prewarm;

    public TextDocumentSyncHandler(DocumentManager documentManager, TranslationPrewarmService? prewarm = null)
    {
        DocumentManager = documentManager;
        _prewarm = prewarm;
    }

    public void DidOpen(DidOpenTextDocumentParams request)
    {
        var text = request.TextDocument.Text ?? string.Empty;
        var uriString = request.TextDocument.Uri.ToString();
        DocumentManager.UpdateDocument(uriString, text);
        _prewarm?.WarmDocument(UriToFilePath(uriString), text);
    }

    public void DidChange(DidChangeTextDocumentParams request)
    {
        var text = request.ContentChanges?.FirstOrDefault()?.Text;
        if (text is null)
        {
            return;
        }

        DocumentManager.UpdateDocument(request.TextDocument.Uri.ToString(), text);
    }

    public void DidClose(DidCloseTextDocumentParams request)
    {
        DocumentManager.RemoveDocument(request.TextDocument.Uri.ToString());
    }

    public void DidSave(DidSaveTextDocumentParams request)
    {
        if (request.Text is null)
        {
            return;
        }

        var uriString = request.TextDocument.Uri.ToString();
        DocumentManager.UpdateDocument(uriString, request.Text);
        _prewarm?.WarmDocument(UriToFilePath(uriString), request.Text);
    }

    private static string UriToFilePath(string uriString)
    {
        try
        {
            return Uri.TryCreate(uriString, UriKind.Absolute, out var uri) ? uri.LocalPath : uriString;
        }
        catch
        {
            return uriString;
        }
    }
}
