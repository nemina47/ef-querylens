using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    private static ExpressionSyntax StripTransparentQueryableCasts(ExpressionSyntax expression)
    {
        var rewritten = new TransparentQueryableCastStripper().Visit(expression) as ExpressionSyntax;
        return rewritten ?? expression;
    }

    /// <summary>
    /// If the expression is an invocation chain whose receiver is (or descends through
    /// parentheses to) an <c>await</c> expression — e.g.
    /// <c>(await query.ToListAsync()).ToList()</c> — returns the operand of that
    /// <c>await</c> (<c>query.ToListAsync()</c>).
    ///
    /// In-memory operations chained after <c>await</c> (e.g. <c>.ToList()</c> on an
    /// already-materialised <c>List&lt;T&gt;</c>) are irrelevant for SQL capture.
    /// The runner template's <c>UnwrapTask</c> helper already handles
    /// <see cref="System.Threading.Tasks.Task{T}"/> results, so stripping the
    /// outer await chain preserves SQL generation while avoiding CS4032
    /// ("The 'await' operator can only be used within an async method") in the
    /// generated synchronous scaffold.
    ///
    /// Returns <paramref name="expression"/> unchanged when no top-level
    /// <c>await</c> is reachable by walking the outer invocation chain.
    /// </summary>
    private static ExpressionSyntax StripOuterAwaitChain(ExpressionSyntax expression)
    {
        var current = expression;
        for (var depth = 0; depth < 16; depth++)
        {
            switch (current)
            {
                case AwaitExpressionSyntax awaitExpr:
                    return awaitExpr.Expression;

                case ParenthesizedExpressionSyntax paren:
                    current = paren.Expression;
                    continue;

                case InvocationExpressionSyntax inv
                    when inv.Expression is MemberAccessExpressionSyntax ma:
                    current = ma.Expression;
                    continue;

                case MemberAccessExpressionSyntax ma:
                    current = ma.Expression;
                    continue;

                default:
                    // Reached a node that is not part of the outer chain
                    // (e.g. an IdentifierName at the root of a normal query).
                    // No top-level await found — return the original expression.
                    return expression;
            }
        }

        return expression;
    }

    private static bool IsTransparentQueryCastType(TypeSyntax type)
    {
        var typeText = type.ToString();
        return typeText.Contains("IQueryable<", StringComparison.Ordinal)
               || typeText.Contains("IOrderedQueryable<", StringComparison.Ordinal)
               || typeText.Contains("IEnumerable<", StringComparison.Ordinal)
               || typeText.Contains("IOrderedEnumerable<", StringComparison.Ordinal)
               || typeText.Contains("IAsyncEnumerable<", StringComparison.Ordinal);
    }

    private static ExpressionSyntax TryInlineLocalQueryRoot(
        ExpressionSyntax expression,
        InvocationExpressionSyntax invocationContext)
    {
        var anchorStatement = invocationContext.FirstAncestorOrSelf<StatementSyntax>();
        if (anchorStatement is null)
        {
            return expression;
        }

        return InlineLeftMostIdentifierChain(expression, anchorStatement);
    }

    private static ExpressionSyntax InlineLeftMostIdentifierChain(
        ExpressionSyntax expression,
        StatementSyntax anchorStatement)
    {
        var currentExpression = expression;
        var currentAnchorStatement = anchorStatement;

        for (var depth = 0; depth < 32; depth++)
        {
            if (!TryGetLeftMostExpression(currentExpression, out var leftMostExpression)
                || leftMostExpression is not IdentifierNameSyntax identifier)
            {
                break;
            }

            if (!TryResolveLocalExpressionCore(
                    identifier.Identifier.ValueText,
                    currentAnchorStatement,
                    out var resolvedExpression,
                    out var resolvedAtStatement)
                || resolvedAtStatement is null)
            {
                break;
            }

            currentExpression = currentExpression.ReplaceNode(leftMostExpression, resolvedExpression.WithoutTrivia());
            currentAnchorStatement = resolvedAtStatement;
        }

        return currentExpression;
    }

    private static bool TryGetLeftMostExpression(ExpressionSyntax expression, out ExpressionSyntax leftMost)
    {
        leftMost = expression;
        var current = expression;

        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation
                    when invocation.Expression is MemberAccessExpressionSyntax memberAccess:
                    current = memberAccess.Expression;
                    continue;

                case MemberAccessExpressionSyntax member:
                    current = member.Expression;
                    continue;

                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case CastExpressionSyntax cast:
                    current = cast.Expression;
                    continue;

                default:
                    leftMost = current;
                    return true;
            }
        }
    }

    private sealed class TransparentQueryableCastStripper : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
        {
            var visited = (CastExpressionSyntax)base.VisitCastExpression(node)!;
            if (!IsTransparentQueryCastType(visited.Type))
                return visited;

            return visited.Expression.WithTriviaFrom(node);
        }
    }
}
