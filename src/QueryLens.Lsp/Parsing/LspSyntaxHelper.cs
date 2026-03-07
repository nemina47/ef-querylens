using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace QueryLens.Lsp.Parsing;

public sealed record SourceUsingContext(
    IReadOnlyList<string> Imports,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<string> StaticTypes);

public static class LspSyntaxHelper
{
    private static readonly HashSet<string> TerminalMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ToList", "ToListAsync", "ToArray", "ToArrayAsync", "ToDictionary", "ToDictionaryAsync",
        "First", "FirstOrDefault", "FirstAsync", "FirstOrDefaultAsync",
        "Single", "SingleOrDefault", "SingleAsync", "SingleOrDefaultAsync",
        "Last", "LastOrDefault", "LastAsync", "LastOrDefaultAsync",
        "Count", "CountAsync", "LongCount", "LongCountAsync",
        "Any", "AnyAsync", "All", "AllAsync",
        "Min", "MinAsync", "Max", "MaxAsync", "Sum", "SumAsync", "Average", "AverageAsync",
        "ElementAt", "ElementAtOrDefault", "ElementAtAsync", "ElementAtOrDefaultAsync",
        "AsEnumerable", "AsAsyncEnumerable", "ToLookup", "ToLookupAsync",
        "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync"
    };

    private static readonly HashSet<string> PredicateTerminalMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Count", "CountAsync", "LongCount", "LongCountAsync",
        "Any", "AnyAsync",
        "First", "FirstOrDefault", "FirstAsync", "FirstOrDefaultAsync",
        "Single", "SingleOrDefault", "SingleAsync", "SingleOrDefaultAsync",
        "Last", "LastOrDefault", "LastAsync", "LastOrDefaultAsync"
    };

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

        // We need the entire chain, so we walk to the top-most invocation/member access
        // Example: db.Orders.Where(x).Select(y) -> We want the whole outer Invocation
        while (targetExpression.Parent is MemberAccessExpressionSyntax ||
               targetExpression.Parent is InvocationExpressionSyntax)
        {
            if (targetExpression.Parent is MemberAccessExpressionSyntax m)
            {
                if (TerminalMethods.Contains(m.Name.Identifier.Text))
                {
                    break;
                }
            }

            targetExpression = (ExpressionSyntax)targetExpression.Parent;
        }

        // Post-process: strip any trailing terminal method calls from the result.
        // This handles hovering directly over a terminal keyword (e.g. "ToList"):
        //   db.Orders.Where(...).ToList()  →  db.Orders.Where(...)
        // The while loop above only guards upward traversal; this handles the case
        // where the starting node is already the outermost terminal invocation.
        while (targetExpression is InvocationExpressionSyntax terminalInvocation &&
               terminalInvocation.Expression is MemberAccessExpressionSyntax terminalAccess &&
               TerminalMethods.Contains(terminalAccess.Name.Identifier.Text))
        {
            if (TryExtractPredicateArgument(
                    terminalAccess.Name.Identifier.Text,
                    terminalInvocation.ArgumentList.Arguments,
                    out var predicateArgument))
            {
                targetExpression = CreateWhereCall(terminalAccess.Expression, predicateArgument);
                continue;
            }

            targetExpression = terminalAccess.Expression;
        }

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

    private static string? TryExtractRootContextVariable(ExpressionSyntax expression)
    {
        var current = expression;
        string? lastMemberName = null;

        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation:
                    current = invocation.Expression;
                    continue;

                case MemberAccessExpressionSyntax memberAccess:
                    lastMemberName = memberAccess.Name.Identifier.Text;
                    current = memberAccess.Expression;
                    continue;

                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.Text;

                case ThisExpressionSyntax:
                    return lastMemberName;

                default:
                    return lastMemberName;
            }
        }
    }

    private static bool TryExtractPredicateArgument(
        string terminalMethodName,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        out ExpressionSyntax predicateArgument)
    {
        predicateArgument = null!;

        if (!PredicateTerminalMethods.Contains(terminalMethodName) || arguments.Count == 0)
            return false;

        foreach (var argument in arguments)
        {
            if (IsCancellationTokenArgument(argument))
                continue;

            // Accept lambdas and common variable/member forms that point to a pre-built predicate.
            if (argument.Expression is LambdaExpressionSyntax
                || argument.Expression is AnonymousMethodExpressionSyntax
                || argument.Expression is IdentifierNameSyntax
                || argument.Expression is MemberAccessExpressionSyntax)
            {
                predicateArgument = argument.Expression;
                return true;
            }
        }

        return false;
    }

    private static bool IsCancellationTokenArgument(ArgumentSyntax argument)
    {
        if (argument.NameColon?.Name.Identifier.ValueText is { } named
            && string.Equals(named, "cancellationToken", StringComparison.OrdinalIgnoreCase))
            return true;

        if (argument.Expression is IdentifierNameSyntax id)
        {
            var n = id.Identifier.ValueText;
            return string.Equals(n, "ct", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(n, "cancellationToken", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static InvocationExpressionSyntax CreateWhereCall(
        ExpressionSyntax source,
        ExpressionSyntax predicate)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                source,
                SyntaxFactory.IdentifierName("Where")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(predicate))));
    }
}
