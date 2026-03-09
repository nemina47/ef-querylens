using System.Collections.Concurrent;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace EFQueryLens.Lsp;

public class DocumentManager
{
    private readonly ConcurrentDictionary<DocumentUri, string> _documents = new();

    public void UpdateDocument(DocumentUri uri, string text)
    {
        _documents[uri] = text;
    }

    public void RemoveDocument(DocumentUri uri)
    {
        _documents.TryRemove(uri, out _);
    }

    public string? GetDocumentText(DocumentUri uri)
    {
        return _documents.TryGetValue(uri, out var text) ? text : null;
    }
}
