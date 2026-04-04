/// <summary>
/// Query Extraction V2 Slice 1 - Syntax-First Extraction IR.
/// 
/// Implements deterministic query extraction without relying on legacy replay heuristics.
/// Core responsibilities:
/// - Classify query boundaries (materialized vs queryable)
/// - Trace from hover position to DbSet query root
/// - Validate helper eligibility (queryable return, direct composition, no control flow)
/// - Report explicit diagnostics for unsupported shapes
/// - Produce V2QueryExtractionPlan IR for downstream capture planning
/// 
/// Slice 1 focuses on extraction IR and validation only; runtime codegen deferred to slice 3.
/// Used by: HoverPreviewService to feed LSP hover requests; capture planner (slice 2) to build symbol classification.
/// </summary>
using System.Collections.Generic;
using System.Linq;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public enum V2QueryBoundaryKind
{
    Queryable,
    Materialized,
}

public sealed record V2ExtractionDiagnostic(string Code, string Message);

public sealed record V2QueryExtractionPlan(
    string Expression,
    string ContextVariableName,
    string RootContextVariableName,
    string RootMemberName,
    string? CallSiteExpression,
    ExtractionOriginSnapshot Origin,
    V2QueryBoundaryKind BoundaryKind,
    bool NeedsMaterialization,
    IReadOnlyList<string> AppliedHelperMethods,
    IReadOnlyList<V2ExtractionDiagnostic> Diagnostics);

public static partial class LspSyntaxHelper
{
    public static bool TryBuildV2ExtractionPlan(
        string sourceText,
        string filePath,
        int line,
        int character,
        out V2QueryExtractionPlan? plan,
        out IReadOnlyList<V2ExtractionDiagnostic> diagnostics,
        ProjectSourceIndex? sourceIndex = null,
        string? targetAssemblyPath = null)
    {
        plan = null;
        var diagList = new List<V2ExtractionDiagnostic>();

        var extraction = TryExtractLinqExpressionDetailed(
            sourceText,
            filePath,
            line,
            character,
            sourceIndex,
            targetAssemblyPath,
            skipV2Plan: true);

        if (extraction is null)
        {
            diagList.Add(new V2ExtractionDiagnostic(
                "QLV2_NO_QUERY",
                "No queryable expression could be extracted at the hover position."));
            diagnostics = diagList;
            return false;
        }

        var helperDiagnostics = AnalyzeHelperEligibility(sourceText, line, character, targetAssemblyPath);
        if (helperDiagnostics.Count > 0)
        {
            diagnostics = helperDiagnostics;
            return false;
        }

        var rootContext = extraction.ContextVariableName;
        var rootMember = extraction.ContextVariableName;
        var appliedHelpers = CollectAppliedHelperMethods(extraction.CallSiteExpression ?? extraction.Expression);
        var boundaryKind = ClassifyBoundary(extraction.Expression);

        try
        {
            var parsed = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression(extraction.Expression);
            if (TryExtractRootContextVariable(parsed) is { Length: > 0 } extractedRoot)
                rootContext = extractedRoot;

            if (TryExtractFirstMemberAfterRoot(parsed, out var member) && !string.IsNullOrWhiteSpace(member))
                rootMember = member;
        }
        catch
        {
            // Best effort: keep extraction-derived values.
        }

        plan = new V2QueryExtractionPlan(
            extraction.Expression,
            extraction.ContextVariableName,
            rootContext,
            rootMember,
            extraction.CallSiteExpression,
            extraction.Origin,
            boundaryKind,
            boundaryKind == V2QueryBoundaryKind.Queryable,
            appliedHelpers,
            []);

        diagnostics = [];
        return true;
    }

    private static V2QueryBoundaryKind ClassifyBoundary(string expression)
    {
        try
        {
            var parsed = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression(expression);
            var invocation = parsed.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation?.Expression is MemberAccessExpressionSyntax access)
            {
                return TerminalMethods.Contains(access.Name.Identifier.ValueText)
                    ? V2QueryBoundaryKind.Materialized
                    : V2QueryBoundaryKind.Queryable;
            }
        }
        catch
        {
            // Fall through to conservative queryable classification.
        }

        return V2QueryBoundaryKind.Queryable;
    }

    private static List<string> CollectAppliedHelperMethods(string callSiteExpression)
    {
        var helpers = new List<string>();
        try
        {
            var parsed = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression(callSiteExpression);
            foreach (var invocation in parsed.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                var method = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    _ => string.Empty,
                };

                if (string.IsNullOrWhiteSpace(method))
                    continue;

                if (QueryChainMethods.Contains(method) || TerminalMethods.Contains(method))
                    continue;

                if (!helpers.Contains(method, StringComparer.Ordinal))
                    helpers.Add(method);
            }
        }
        catch
        {
            // Best effort only.
        }

        return helpers;
    }

    private static IReadOnlyList<V2ExtractionDiagnostic> AnalyzeHelperEligibility(
        string sourceText,
        int line,
        int character,
        string? targetAssemblyPath)
    {
        var diagnostics = new List<V2ExtractionDiagnostic>();

        try
        {
            TryCreateSemanticModel(sourceText, targetAssemblyPath, out var tree, out var root, out var model);
            var textLines = tree.GetText().Lines;
            if (line < 0 || line >= textLines.Count)
                return diagnostics;

            var charOffset = Math.Min(Math.Max(character, 0), textLines[line].End - textLines[line].Start);
            var position = textLines[line].Start + charOffset;
            var node = root.FindToken(position).Parent;
            if (node is null)
                return diagnostics;

            var helperInvocations = node.AncestorsAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .DistinctBy(i => (i.SpanStart, i.Span.Length))
                .Where(i => TryGetInvokedMethodName(i) is { Length: > 0 } methodName
                    && !QueryChainMethods.Contains(methodName)
                    && !TerminalMethods.Contains(methodName))
                .ToArray();

            foreach (var invocation in helperInvocations)
            {
                var declarations = ResolveHelperMethodDeclarations(invocation, model, currentFilePath: null);
                if (declarations.Count == 0 && TryGetInvokedMethodName(invocation) is { Length: > 0 } methodName)
                {
                    // Fallback for incomplete snippets where semantic binding may fail.
                    declarations = root.DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .Where(m => string.Equals(m.Identifier.ValueText, methodName, StringComparison.Ordinal))
                        .ToArray();
                }

                foreach (var declaration in declarations)
                {
                    var returnTypeText = declaration.ReturnType?.ToString() ?? string.Empty;
                    if (returnTypeText.Contains("Task<IQueryable<", StringComparison.Ordinal)
                        || returnTypeText.Contains("ValueTask<IQueryable<", StringComparison.Ordinal))
                    {
                        diagnostics.Add(new V2ExtractionDiagnostic(
                            "QLV2_UNSUPPORTED_ASYNC_QUERY_HELPER",
                            $"Helper '{declaration.Identifier.ValueText}' returns async query shape which is unsupported in slice 1."));
                        continue;
                    }

                    if (!LooksLikeQueryableReturnType(declaration.ReturnType))
                    {
                        diagnostics.Add(new V2ExtractionDiagnostic(
                            "QLV2_UNSUPPORTED_NON_QUERY_HELPER",
                            $"Helper '{declaration.Identifier.ValueText}' does not return IQueryable and cannot be inlined in slice 1."));
                        continue;
                    }

                    if (HasUnsupportedControlFlow(declaration))
                    {
                        diagnostics.Add(new V2ExtractionDiagnostic(
                            "QLV2_UNSUPPORTED_HELPER_CONTROL_FLOW",
                            $"Helper '{declaration.Identifier.ValueText}' uses control flow or procedural setup unsupported in slice 1."));
                        continue;
                    }

                    if (TryFindPrimaryQueryInvocation(declaration) is null)
                    {
                        diagnostics.Add(new V2ExtractionDiagnostic(
                            "QLV2_UNSUPPORTED_HELPER_BODY",
                            $"Helper '{declaration.Identifier.ValueText}' does not expose a directly composable query return expression."));
                    }
                }
            }
        }
        catch
        {
            // Best effort - avoid false negatives from analysis failures.
        }

        return diagnostics;
    }

    private static string? TryGetInvokedMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null,
        };
    }

    private static bool HasUnsupportedControlFlow(MethodDeclarationSyntax declaration)
    {
        if (declaration.Body is null)
            return false;

        return declaration.Body.DescendantNodes().Any(n =>
            n is IfStatementSyntax
                or SwitchStatementSyntax
                or ForStatementSyntax
                or ForEachStatementSyntax
                or WhileStatementSyntax
                or DoStatementSyntax
                or TryStatementSyntax
                or LocalFunctionStatementSyntax);
    }
}