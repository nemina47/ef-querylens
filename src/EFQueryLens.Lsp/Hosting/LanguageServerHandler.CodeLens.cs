using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EFQueryLens.Lsp.Parsing;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;

namespace EFQueryLens.Lsp.Hosting;

internal sealed partial class LanguageServerHandler
{
    [JsonRpcMethod(Methods.TextDocumentCodeLensName, UseSingleObjectParameterDeserialization = true)]
    public CodeLens[] GetCodeLens(CodeLensParams request)
    {
        var sourceText = _textSync.DocumentManager.GetDocumentText(request.TextDocument.Uri.ToString());
        if (sourceText is null)
        {
            return Array.Empty<CodeLens>();
        }

        var chains = LspSyntaxHelper.FindAllLinqChains(sourceText);

        var result = new List<CodeLens>();
        foreach (var chain in chains)
        {
            var range = new Microsoft.VisualStudio.LanguageServer.Protocol.Range
            {
                Start = new Position(chain.BadgeLine, chain.BadgeCharacter),
                End = new Position(chain.BadgeLine, chain.BadgeCharacter)
            };

            var arg = new object[]
            {
                new TextDocumentPositionParams
                {
                    TextDocument = request.TextDocument,
                    Position = new Position(chain.Line, chain.Character)
                }
            };

            // 1. SQL Preview — shows popup inline
            result.Add(new CodeLens
            {
                Range = range,
                Command = new Command
                {
                    Title = "SQL Preview",
                    CommandIdentifier = "efquerylens.showsqlpopup",
                    Arguments = arg
                }
            });

            // 2. Open SQL — opens in editor tab
            result.Add(new CodeLens
            {
                Range = range,
                Command = new Command
                {
                    Title = "Open SQL",
                    CommandIdentifier = "efquerylens.opensqleditor",
                    Arguments = arg
                }
            });

            // 3. Copy SQL
            result.Add(new CodeLens
            {
                Range = range,
                Command = new Command
                {
                    Title = "Copy SQL",
                    CommandIdentifier = "efquerylens.copysql",
                    Arguments = arg
                }
            });

            // 4. Analyze
            result.Add(new CodeLens
            {
                Range = range,
                Command = new Command
                {
                    Title = "Analyze",
                    CommandIdentifier = "efquerylens.reanalyze",
                    Arguments = arg
                }
            });
        }

        return result.ToArray();
    }
}
