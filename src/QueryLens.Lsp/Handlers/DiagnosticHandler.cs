using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using QueryLens.Core;

namespace QueryLens.Lsp.Handlers;

internal sealed class DiagnosticHandler : TextDocumentSyncHandlerBase
{
    private readonly ILanguageServerFacade _server;
    private readonly IQueryLensEngine _engine;
    private readonly DocumentManager _documentManager;

    public DiagnosticHandler(ILanguageServerFacade server, IQueryLensEngine engine, DocumentManager documentManager)
    {
        _server = server;
        _engine = engine;
        _documentManager = documentManager;
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        _documentManager.UpdateDocument(request.TextDocument.Uri, request.TextDocument.Text);
        _ = ProcessDocumentAsync(request.TextDocument.Uri, request.TextDocument.Text, cancellationToken);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var text = request.ContentChanges.FirstOrDefault()?.Text;
        if (text != null)
        {
            _documentManager.UpdateDocument(request.TextDocument.Uri, text);
            _ = ProcessDocumentAsync(request.TextDocument.Uri, text, cancellationToken);
        }

        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        if (request.Text != null)
        {
            _documentManager.UpdateDocument(request.TextDocument.Uri, request.Text);
            _ = ProcessDocumentAsync(request.TextDocument.Uri, request.Text, cancellationToken);
        }

        return Unit.Task;
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _documentManager.RemoveDocument(request.TextDocument.Uri);

        // Clear diagnostics when a file closes
        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = request.TextDocument.Uri,
            Diagnostics = new Container<Diagnostic>()
        });

        return Unit.Task;
    }

    private async Task ProcessDocumentAsync(DocumentUri uri, string text, CancellationToken ct)
    {
        // 1. Roslyn parse document to find LINQ statements...
        // 2. Evaluate with QueryLensEngine.TranslateAsync
        // 3. Translate QueryWarning to LSP Diagnostic.
        // For now, this is a stub.

        await Task.Yield();
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TextDocumentSyncRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("csharp"),
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = true }
        };
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new TextDocumentAttributes(uri, "csharp");
    }
}
