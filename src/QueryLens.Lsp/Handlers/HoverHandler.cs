using System.Text.RegularExpressions;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using QueryLens.Core;
using QueryLens.Lsp.Parsing;

namespace QueryLens.Lsp.Handlers;

internal enum InlineMode { Direct, Optimistic, Conservative }

internal sealed class HoverHandler : HoverHandlerBase
{
    private readonly IQueryLensEngine _engine;
    private readonly DocumentManager _documentManager;

    public HoverHandler(ILanguageServerFacade server, IQueryLensEngine engine, DocumentManager documentManager)
    {
        _engine = engine;
        _documentManager = documentManager;
    }

    public override async Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        // 1. Get the document path and live text
        var filePath = request.TextDocument.Uri.GetFileSystemPath();

        var sourceText = _documentManager.GetDocumentText(request.TextDocument.Uri);
        if (sourceText == null) return null;

        // 2. Parse the text dynamically and find the LINQ expression under the cursor
        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            sourceText,
            request.Position.Line,
            request.Position.Character,
            out var contextVariableName);

        if (expression == null || contextVariableName == null)
        {
            // The user isn't hovering over a valid queryable member access chain
            return null;
        }

        var originalExpression = expression;
        string? conservativeExpression = null;
        string? conservativeContextVariableName = null;
        var inlineMode = InlineMode.Direct;

        if (MethodQueryInliner.TryInlineTopLevelInvocation(
                sourceText,
                filePath,
                expression,
                substituteSelectorArguments: true,
                out var inlinedExpression,
                out var inlinedContextVariable,
                out _))
        {
            expression = inlinedExpression;
            inlineMode = InlineMode.Optimistic;
            if (!string.IsNullOrWhiteSpace(inlinedContextVariable))
            {
                contextVariableName = inlinedContextVariable;
            }

            if (MethodQueryInliner.TryInlineTopLevelInvocation(
                    sourceText,
                    filePath,
                    originalExpression,
                    substituteSelectorArguments: false,
                    out var conservativeInlinedExpression,
                    out var conservativeInlinedContextVariable,
                    out _)
                && !string.Equals(conservativeInlinedExpression, expression, StringComparison.Ordinal))
            {
                conservativeExpression = conservativeInlinedExpression;
                conservativeContextVariableName = conservativeInlinedContextVariable;
            }
        }
        else if (MethodQueryInliner.TryInlineTopLevelInvocation(
                     sourceText,
                     filePath,
                     expression,
                     substituteSelectorArguments: false,
                     out var conservativeOnlyInlinedExpression,
                     out var conservativeOnlyInlinedContextVariable,
                     out _))
        {
            expression = conservativeOnlyInlinedExpression;
            inlineMode = InlineMode.Conservative;
            if (!string.IsNullOrWhiteSpace(conservativeOnlyInlinedContextVariable))
            {
                contextVariableName = conservativeOnlyInlinedContextVariable;
            }
        }

        var targetAssembly = AssemblyResolver.TryGetTargetAssembly(filePath);

        // Debug fallback
        if (!string.IsNullOrEmpty(targetAssembly) && targetAssembly.StartsWith("DEBUG_FAIL"))
        {
            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = $"⚠️ *QueryLens AssemblyResolver Failed*\n```text\n{targetAssembly}\n```"
                })
            };
        }

        // Let's protect against the scenario where the assembly isn't built yet
        if (string.IsNullOrEmpty(targetAssembly) || !File.Exists(targetAssembly))
        {
            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value =
                        $"⚠️ *QueryLens: Target assembly `{Path.GetFileName(targetAssembly)}` not found. Please build the project.*"
                })
            };
        }

        var targetAssemblyPath = targetAssembly;

        try
        {
            async Task<QueryTranslationResult> TranslateAsync(string expr, string? ctxVar)
            {
                return await _engine.TranslateAsync(new TranslationRequest
                {
                    AssemblyPath = targetAssemblyPath,
                    Expression = expr,
                    ContextVariableName = ctxVar ?? "db"
                }, cancellationToken);
            }

            var translation = await TranslateAsync(expression, contextVariableName);

            if (!translation.Success &&
                !string.IsNullOrWhiteSpace(conservativeExpression) &&
                !string.Equals(conservativeExpression, expression, StringComparison.Ordinal))
            {
                var fallbackTranslation = await TranslateAsync(
                    conservativeExpression,
                    string.IsNullOrWhiteSpace(conservativeContextVariableName)
                        ? contextVariableName
                        : conservativeContextVariableName);

                if (fallbackTranslation.Success)
                {
                    translation = fallbackTranslation;
                    expression = conservativeExpression;
                    contextVariableName = conservativeContextVariableName ?? contextVariableName;
                    inlineMode = InlineMode.Conservative;
                }
            }

            if (translation.Success)
            {
                var commands = translation.Commands.Count > 0
                    ? translation.Commands
                    : translation.Sql is null
                        ? []
                        : [new QuerySqlCommand { Sql = translation.Sql, Parameters = translation.Parameters }];

                if (commands.Count == 0)
                {
                    return new Hover
                    {
                        Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                        {
                            Kind = MarkupKind.Markdown,
                            Value = "**QueryLens Error**\n```text\nNo SQL was produced for this expression.\n```"
                        })
                    };
                }

                return new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = BuildSqlPreview(commands, inlineMode)
                    })
                };
            }

            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value =
                        $"**QueryLens Error**\n```text\n{translation.ErrorMessage}\n```\n\n*Assembly: `{targetAssembly}`*\n*Expression: `{expression}`*"
                })
            };
        }
        catch (Exception ex)
        {
            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value =
                        $"**QueryLens Exception**\n```text\n{ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.Message ?? ""}\n```\n\n*Assembly: `{targetAssembly}`*\n*Expression: `{expression}`*"
                })
            };
        }
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(HoverCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("csharp")
        };
    }

    private static string BuildSqlPreview(IReadOnlyList<QuerySqlCommand> commands, InlineMode mode)
    {
        var tableNames = ExtractTableNames(commands);
        var tablesSummary = tableNames.Count > 0 ? " · " + string.Join(", ", tableNames) : "";
        var modeLabel = mode switch
        {
            InlineMode.Optimistic => "optimistic-inline",
            InlineMode.Conservative => "conservative-inline",
            _ => "direct"
        };

        string sqlBlock;
        if (commands.Count == 1)
        {
            sqlBlock = commands[0].Sql;
        }
        else
        {
            var parts = new string[commands.Count];
            for (var i = 0; i < commands.Count; i++)
            {
                parts[i] = $"-- {CircledNumber(i + 1)}\n{commands[i].Sql}";
            }
            sqlBlock = string.Join("\n\n", parts);
        }

        var statementWord = commands.Count == 1 ? "query" : "queries";
        return $"**QueryLens · {commands.Count} {statementWord}**{tablesSummary}\n```sql\n{sqlBlock}\n```\n\n*mode: {modeLabel}*";
    }

    private static string CircledNumber(int n) => n switch
    {
        1 => "①", 2 => "②", 3 => "③", 4 => "④", 5 => "⑤",
        6 => "⑥", 7 => "⑦", 8 => "⑧", 9 => "⑨", _ => $"({n})"
    };

    private static readonly Regex TableNameRegex =
        new(@"(?:FROM|JOIN)\s+`([^`]+)`", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IReadOnlyList<string> ExtractTableNames(IReadOnlyList<QuerySqlCommand> commands)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var cmd in commands)
        {
            foreach (Match m in TableNameRegex.Matches(cmd.Sql))
            {
                var name = m.Groups[1].Value;
                if (seen.Add(name)) ordered.Add(name);
            }
        }
        return ordered;
    }
}
