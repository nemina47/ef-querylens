using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using EFQueryLens.Core;
using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Lsp.Handlers;

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
        var topLevelInliningSucceeded = false;
        var optimisticInlineDiagnostics = new MethodQueryInliner.InlineSelectorDiagnostics();
        var conservativeInlineDiagnostics = new MethodQueryInliner.InlineSelectorDiagnostics();
        var inlineContainsNestedSelectorMaterialization = false;

        var usingImports = new HashSet<string>(StringComparer.Ordinal);
        var usingAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var usingStaticTypes = new HashSet<string>(StringComparer.Ordinal);

        MergeUsingContext(
            LspSyntaxHelper.ExtractUsingContext(sourceText),
            usingImports,
            usingAliases,
            usingStaticTypes,
            aliasPriority: AliasMergePriority.PreferExisting);

        string? selectedInlinedSourcePath = null;

        if (MethodQueryInliner.TryInlineTopLevelInvocation(
                sourceText,
                filePath,
                expression,
                substituteSelectorArguments: true,
                out var inlinedExpression,
                out var inlinedContextVariable,
                out var optimisticSourcePath,
                out optimisticInlineDiagnostics,
                out _))
        {
            topLevelInliningSucceeded = true;
            expression = inlinedExpression;
            inlineMode = InlineMode.Optimistic;
            inlineContainsNestedSelectorMaterialization =
                optimisticInlineDiagnostics.ContainsNestedSelectorMaterialization;
            selectedInlinedSourcePath = optimisticSourcePath;
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
                    out var conservativeSourcePath,
                    out conservativeInlineDiagnostics,
                    out _)
                && !string.Equals(conservativeInlinedExpression, expression, StringComparison.Ordinal))
            {
                conservativeExpression = conservativeInlinedExpression;
                conservativeContextVariableName = conservativeInlinedContextVariable;
                if (string.IsNullOrWhiteSpace(selectedInlinedSourcePath))
                {
                    selectedInlinedSourcePath = conservativeSourcePath;
                }
            }
        }
        else if (MethodQueryInliner.TryInlineTopLevelInvocation(
                     sourceText,
                     filePath,
                     expression,
                     substituteSelectorArguments: false,
                     out var conservativeOnlyInlinedExpression,
                     out var conservativeOnlyInlinedContextVariable,
                     out var conservativeOnlySourcePath,
                     out conservativeInlineDiagnostics,
                     out _))
        {
            expression = conservativeOnlyInlinedExpression;
            inlineMode = InlineMode.Conservative;
            inlineContainsNestedSelectorMaterialization =
                conservativeInlineDiagnostics.ContainsNestedSelectorMaterialization;
            selectedInlinedSourcePath = conservativeOnlySourcePath;
            if (!string.IsNullOrWhiteSpace(conservativeOnlyInlinedContextVariable))
            {
                contextVariableName = conservativeOnlyInlinedContextVariable;
            }
        }

        // Suppress QueryLens hover output for non-query expressions.
        // Top-level service calls that can be inlined are still supported because
        // the inlined expression is checked here.
        if (!topLevelInliningSucceeded
            && !LspSyntaxHelper.IsLikelyDbContextRootIdentifier(contextVariableName))
        {
            return null;
        }

        if (!LspSyntaxHelper.IsLikelyQueryPreviewCandidate(expression))
        {
            return null;
        }

        TryMergeUsingContextFromFile(
            selectedInlinedSourcePath,
            filePath,
            usingImports,
            usingAliases,
            usingStaticTypes);

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
                    ContextVariableName = ctxVar ?? "db",
                    AdditionalImports = usingImports.ToArray(),
                    UsingAliases = new Dictionary<string, string>(usingAliases, StringComparer.Ordinal),
                    UsingStaticTypes = usingStaticTypes.ToArray()
                }, cancellationToken);
            }

            var translation = await TranslateAsync(expression, contextVariableName);

            if (translation.Success &&
                inlineMode == InlineMode.Optimistic &&
                inlineContainsNestedSelectorMaterialization &&
                !string.IsNullOrWhiteSpace(conservativeExpression) &&
                !string.Equals(conservativeExpression, expression, StringComparison.Ordinal))
            {
                var conservativeHasSelectorPlaceholder =
                    LooksLikeSelectorPlaceholderQuery(conservativeExpression);

                if (!conservativeHasSelectorPlaceholder)
                {
                    var conservativeTranslation = await TranslateAsync(
                        conservativeExpression,
                        string.IsNullOrWhiteSpace(conservativeContextVariableName)
                            ? contextVariableName
                            : conservativeContextVariableName);

                    if (conservativeTranslation.Success &&
                        conservativeTranslation.Commands.Count > translation.Commands.Count)
                    {
                        translation = conservativeTranslation;
                        expression = conservativeExpression;
                        contextVariableName = conservativeContextVariableName ?? contextVariableName;
                        inlineMode = InlineMode.Conservative;
                        inlineContainsNestedSelectorMaterialization =
                            conservativeInlineDiagnostics.ContainsNestedSelectorMaterialization;
                    }
                }
            }

            if (!translation.Success &&
                !string.IsNullOrWhiteSpace(conservativeExpression) &&
                !string.Equals(conservativeExpression, expression, StringComparison.Ordinal))
            {
                var conservativeHasSelectorPlaceholder =
                    LooksLikeSelectorPlaceholderQuery(conservativeExpression);

                var fallbackTranslation = await TranslateAsync(
                    conservativeExpression,
                    string.IsNullOrWhiteSpace(conservativeContextVariableName)
                        ? contextVariableName
                        : conservativeContextVariableName);

                if (fallbackTranslation.Success && !conservativeHasSelectorPlaceholder)
                {
                    translation = fallbackTranslation;
                    expression = conservativeExpression;
                    contextVariableName = conservativeContextVariableName ?? contextVariableName;
                    inlineMode = InlineMode.Conservative;
                    inlineContainsNestedSelectorMaterialization =
                        conservativeInlineDiagnostics.ContainsNestedSelectorMaterialization;
                }
            }

            if (translation.Success)
            {
                if (inlineContainsNestedSelectorMaterialization)
                {
                    translation = AppendWarningIfMissing(
                        translation,
                        new QueryWarning
                        {
                            Severity = WarningSeverity.Warning,
                            Code = "QL_EXPRESSION_PARTIAL_RISK",
                            Message = "Expression selector contains nested materialization that may require additional SQL commands.",
                            Suggestion = "SQL preview is best-effort for this projection shape; child collection commands may be omitted offline.",
                        });
                }

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
                        Value = BuildSqlPreview(
                            commands,
                            inlineMode,
                            translation.Warnings,
                            request.TextDocument.Uri.ToString(),
                            request.Position.Line,
                            request.Position.Character,
                            originalExpression,
                            expression)
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

    private static string BuildSqlPreview(
        IReadOnlyList<QuerySqlCommand> commands,
        InlineMode mode,
        IReadOnlyList<QueryWarning> warnings,
        string documentUri,
        int line,
        int character,
        string sourceExpression,
        string executedExpression)
    {
        var tableNames = ExtractTableNames(commands);
        var tablesSummary = tableNames.Count > 0 ? " · " + string.Join(", ", tableNames) : "";
        var modeLabel = mode switch
        {
            InlineMode.Optimistic => "optimistic-inline",
            InlineMode.Conservative => "conservative-inline",
            _ => "direct"
        };
        var modeDescription = mode switch
        {
            InlineMode.Optimistic => "Best-effort inline translation with selector substitution.",
            InlineMode.Conservative => "Safer inline translation without selector substitution.",
            _ => "Direct translation of the hovered query chain."
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
                var role = InferSplitQueryRole(commands[i].Sql, i, commands.Count);
                parts[i] = $"-- ===== Split Query {i + 1} of {commands.Count} ({role}) =====\n{commands[i].Sql}";
            }
            sqlBlock = string.Join("\n\n", parts);
        }

        var statementWord = commands.Count == 1 ? "query" : "queries";

        var warningNotes = warnings
            .Select(w => string.IsNullOrWhiteSpace(w.Suggestion)
                ? $"{w.Code}: {w.Message}"
                : $"{w.Code}: {w.Message} ({w.Suggestion})")
            .ToList();

        var warningLines = warningNotes
            .Select(note => $"- {note}")
            .ToList();

        var usedToQueryStringFallback = warnings.Any(w =>
            string.Equals(w.Code, "QL_CAPTURE_FALLBACK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.Code, "QL_CAPTURE_SKIPPED", StringComparison.OrdinalIgnoreCase));

        var containsSplitQueryNotice = commands.Count == 1 &&
                                       commands[0].Sql.Contains("split-query mode", StringComparison.OrdinalIgnoreCase);

        if (usedToQueryStringFallback && containsSplitQueryNotice)
        {
            var splitQueryPartialNote = "QL_SPLIT_QUERY_PARTIAL: Only the first split-query SQL statement is shown because execution capture was unavailable.";
            warningNotes.Add(splitQueryPartialNote);
            warningLines.Add($"- {splitQueryPartialNote}");
        }

        if (warningLines.Count == 0)
        {
            return $"**QueryLens · {commands.Count} {statementWord}**{tablesSummary}\n*LINQ-to-SQL strategy: {modeLabel}* - {modeDescription}\n{BuildHoverActions(documentUri, line, character, sourceExpression, executedExpression, modeLabel, modeDescription, warningNotes)}\n\n```sql\n{sqlBlock}\n```";
        }

        return $"**QueryLens · {commands.Count} {statementWord}**{tablesSummary}\n*LINQ-to-SQL strategy: {modeLabel}* - {modeDescription}\n{BuildHoverActions(documentUri, line, character, sourceExpression, executedExpression, modeLabel, modeDescription, warningNotes)}\n\n```sql\n{sqlBlock}\n```\n\n**Notes**\n{string.Join("\n", warningLines)}";
    }

    private static string BuildHoverActions(
        string uri,
        int line,
        int character,
        string sourceExpression,
        string executedExpression,
        string modeLabel,
        string modeDescription,
        IReadOnlyList<string>? warningNotes = null)
    {
        var encodedArgs = Uri.EscapeDataString(JsonSerializer.Serialize(new object[] { uri, line, character }));
        var copyCommandUri = $"command:efquerylens.copySql?{encodedArgs}";
        var openEditorCommandUri = $"command:efquerylens.openSqlEditor?{encodedArgs}";
        var metadataPayload = BuildHoverMetadataPayload(sourceExpression, executedExpression, modeLabel, modeDescription, warningNotes);
        return $"[Copy SQL]({copyCommandUri}) | [Open SQL Editor]({openEditorCommandUri})\n<!--QUERYLENS_META:{metadataPayload}-->";
    }

    private static string BuildHoverMetadataPayload(
        string sourceExpression,
        string executedExpression,
        string modeLabel,
        string modeDescription,
        IReadOnlyList<string>? warningNotes = null)
    {
        var formattedExecutedExpression = FormatLinqExpression(executedExpression);
        var json = JsonSerializer.Serialize(new HoverActionMetadata(
            sourceExpression,
            formattedExecutedExpression,
            modeLabel,
            modeDescription,
            (warningNotes ?? []).ToArray()));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static string FormatLinqExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return expression;
        }

        try
        {
            var root = CSharpSyntaxTree.ParseText(expression).GetRoot();
            return root.NormalizeWhitespace("    ", "\n", false).ToFullString();
        }
        catch
        {
            return expression.Trim();
        }
    }

    private static string InferSplitQueryRole(string sql, int index, int total)
    {
        if (index == 0)
        {
            return total > 1 ? "root" : "single";
        }

        if (sql.Contains("JOIN", StringComparison.OrdinalIgnoreCase))
        {
            return "include";
        }

        if (sql.Contains(" IN (", StringComparison.OrdinalIgnoreCase)
            || sql.Contains(" EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            return "related";
        }

        return "related";
    }

    private sealed record HoverActionMetadata(
        string SourceExpression,
        string ExecutedExpression,
        string Mode,
        string ModeDescription,
        string[] Warnings);

    private static readonly Regex TableNameRegex =
        new(@"(?:FROM|JOIN)\s+`([^`]+)`", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SelectorPlaceholderRegex =
        new(@"\.Select\s*\(\s*(expression|selector|projection)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    private static bool LooksLikeSelectorPlaceholderQuery(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        return SelectorPlaceholderRegex.IsMatch(expression);
    }

    private static void TryMergeUsingContextFromFile(
        string? sourceFilePath,
        string currentFilePath,
        ISet<string> imports,
        IDictionary<string, string> aliases,
        ISet<string> staticTypes)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) ||
            string.Equals(sourceFilePath, currentFilePath, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(sourceFilePath))
        {
            return;
        }

        try
        {
            var source = File.ReadAllText(sourceFilePath);
            var context = LspSyntaxHelper.ExtractUsingContext(source);
            MergeUsingContext(context, imports, aliases, staticTypes, AliasMergePriority.PreferIncoming);
        }
        catch
        {
            // Best effort only; hover translation continues without extra using context.
        }
    }

    private static void MergeUsingContext(
        SourceUsingContext context,
        ISet<string> imports,
        IDictionary<string, string> aliases,
        ISet<string> staticTypes,
        AliasMergePriority aliasPriority)
    {
        foreach (var import in context.Imports)
        {
            imports.Add(import);
        }

        foreach (var staticType in context.StaticTypes)
        {
            staticTypes.Add(staticType);
        }

        foreach (var (alias, target) in context.Aliases)
        {
            if (aliasPriority == AliasMergePriority.PreferIncoming)
            {
                aliases[alias] = target;
            }
            else if (!aliases.ContainsKey(alias))
            {
                aliases[alias] = target;
            }
        }
    }

    private enum AliasMergePriority
    {
        PreferExisting,
        PreferIncoming
    }

    private static QueryTranslationResult AppendWarningIfMissing(
        QueryTranslationResult translation,
        QueryWarning warning)
    {
        if (translation.Warnings.Any(w => string.Equals(w.Code, warning.Code, StringComparison.OrdinalIgnoreCase)))
        {
            return translation;
        }

        var warnings = translation.Warnings.Concat([warning]).ToArray();
        return translation with { Warnings = warnings };
    }
}
