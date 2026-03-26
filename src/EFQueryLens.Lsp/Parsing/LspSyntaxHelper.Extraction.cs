using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    public static string? TryExtractLinqExpression(string sourceText, int line, int character,
        out string? contextVariableName)
    {
        contextVariableName = null;

        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetRoot();

        var textLines = sourceText.Split('\n');
        if (line >= textLines.Length) return null;

        var textLine = textLines[line];
        if (character > textLine.Length) return null;

        // Find the absolute position from Line/Char
        var position = tree.GetText().Lines[line].Start + character;

        // Find the node at the cursor position
        var node = root.FindToken(position).Parent;

        // Walk up until we find an InvocationExpression (like .Where() or .ToList())
        // or a MemberAccessExpression (like db.Orders)
        var invocation = node?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        var memberAccess = node?.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();

        ExpressionSyntax? targetExpression = invocation ?? (ExpressionSyntax?)memberAccess;

        if (targetExpression == null)
            return null;

        // Walk to the topmost invocation/member access, including any terminal call
        // (Count, ToList, FirstOrDefaultAsync, ExecuteDeleteAsync, etc.) so the engine
        // receives the exact expression the app runs and generates the real SQL.
        while (targetExpression.Parent is MemberAccessExpressionSyntax or InvocationExpressionSyntax)
        {
            targetExpression = (ExpressionSyntax)targetExpression.Parent;
        }

        // Guard: reject expressions that are not LINQ query chains.
        // Without this, hovering inside a lambda argument of a non-LINQ method call
        // (e.g. "x => new Dto{...}" passed to GetFooAsync(id, x => new Dto{...}, ct))
        // causes the entire call site to be extracted as the LINQ expression, with the
        // method name mis-identified as the DbContext variable name. The engine then
        // declares a variable using that name and later tries to invoke it as a method,
        // producing CS0149: Method name expected.
        // GetInvocationChainMethodNames only yields for member-access chains (a.b.c()),
        // so a bare call like GetFooAsync(...) yields nothing → IsLikelyQueryChain = false.
        if (targetExpression is InvocationExpressionSyntax finalInvocation
            && !IsLikelyQueryChain(finalInvocation))
        {
            if (TryExtractFromExpressionParameterHelperCall(
                    root,
                    finalInvocation,
                    position,
                    out var synthesizedExpression,
                    out var synthesizedContextVariableName))
            {
                contextVariableName = synthesizedContextVariableName;
                return synthesizedExpression;
            }

            // The cursor is inside a nested call (e.g. a predicate method inside a lambda
            // argument: "w.IsNotDeleted()" inside ".Where(w => w.IsNotDeleted())").
            // Walk up through ancestors to find a containing LINQ query chain — this
            // handles hovering on any token within a .Where(…) or .Select(…) lambda
            // in Visual Studio / Rider, where the QuickInfo trigger fires on the exact
            // token under the cursor rather than the method name.
            var outerChain = node?.AncestorsAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Select(GetOutermostInvocationChain)
                .FirstOrDefault(IsLikelyQueryChain);

            if (outerChain is null)
            {
                return null;
            }

            targetExpression = outerChain;
        }

        // Inline local IQueryable variables for non-terminal chains too, so
        // expressions like auditTrailQuery.ApplyPaging(...).ToListAsync(...) are
        // rooted back to dbContext.* and keep DbContext discovery deterministic.
        if (invocation is not null)
        {
            targetExpression = TryInlineLocalQueryRoot(targetExpression, invocation);
        }

        targetExpression = StripTransparentQueryableCasts(targetExpression);

        // Identify the root variable from the left-most chain segment.
        // Using DescendantNodes().FirstOrDefault() can pick lambda identifiers
        // (e.g. "s") depending on cursor position and trivia layout.
        contextVariableName = TryExtractRootContextVariable(targetExpression)
            ?? targetExpression.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Select(i => i.Identifier.Text)
                .FirstOrDefault();

        return targetExpression.ToString();
    }

    public static SourceUsingContext ExtractUsingContext(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetRoot();

        var imports = new List<string>();
        var importSet = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var staticTypes = new List<string>();
        var staticSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var name = usingDirective.Name?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (usingDirective.Alias is { Name.Identifier.ValueText: { Length: > 0 } aliasName })
            {
                aliases[aliasName] = name;
                continue;
            }

            if (!usingDirective.StaticKeyword.IsKind(SyntaxKind.None))
            {
                if (staticSet.Add(name))
                {
                    staticTypes.Add(name);
                }

                continue;
            }

            if (importSet.Add(name))
            {
                imports.Add(name);
            }
        }

        // Add namespaces declared in the file itself. Code inside a namespace can
        // use extension methods from that same namespace without an explicit using,
        // but QueryLens compiles generated snippets in the global namespace, so we
        // need to import these explicitly to preserve behavior.
        foreach (var namespaceDecl in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            var ns = namespaceDecl.Name.ToString();
            if (string.IsNullOrWhiteSpace(ns))
            {
                continue;
            }

            if (importSet.Add(ns))
            {
                imports.Add(ns);
            }
        }

        return new SourceUsingContext(imports, aliases, staticTypes);
    }
}
