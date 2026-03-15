using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace EFQueryLens.Lsp.Hosting;

internal sealed partial class LanguageServerHandler
{
    // Text document sync notifications
    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(DidOpenTextDocumentParams p) => _textSync.DidOpen(p);

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams p) => _textSync.DidChange(p);

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams p) => _textSync.DidClose(p);

    [JsonRpcMethod("textDocument/didSave", UseSingleObjectParameterDeserialization = true)]
    public void DidSave(DidSaveTextDocumentParams p) => _textSync.DidSave(p);

    [JsonRpcMethod("workspace/didChangeConfiguration", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeConfiguration(JToken? _ = null) { }
}
