using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
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

                case QueryExpressionSyntax queryExpression:
                    current = queryExpression.FromClause.Expression;
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

    private static bool TryExtractFirstMemberAfterRoot(
        ExpressionSyntax expression,
        out string memberName)
    {
        memberName = string.Empty;
        var current = expression;

        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation
                    when invocation.Expression is MemberAccessExpressionSyntax memberAccess:
                    if (memberAccess.Expression is IdentifierNameSyntax or ThisExpressionSyntax)
                    {
                        memberName = memberAccess.Name.Identifier.Text;
                        return true;
                    }

                    current = memberAccess.Expression;
                    continue;

                case MemberAccessExpressionSyntax memberAccess:
                    if (memberAccess.Expression is IdentifierNameSyntax or ThisExpressionSyntax)
                    {
                        memberName = memberAccess.Name.Identifier.Text;
                        return true;
                    }

                    current = memberAccess.Expression;
                    continue;

                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case QueryExpressionSyntax queryExpression:
                    current = queryExpression.FromClause.Expression;
                    continue;

                case CastExpressionSyntax cast:
                    current = cast.Expression;
                    continue;

                default:
                    return false;
            }
        }
    }

    private static bool IsInsideLambda(SyntaxNode node)
    {
        return node.Ancestors().Any(a =>
            a is SimpleLambdaExpressionSyntax
                or ParenthesizedLambdaExpressionSyntax
                or AnonymousMethodExpressionSyntax);
    }

    private static InvocationExpressionSyntax GetOutermostInvocationChain(InvocationExpressionSyntax invocation)
    {
        var current = invocation;
        while (current.Parent is MemberAccessExpressionSyntax or InvocationExpressionSyntax)
        {
            if (current.Parent is InvocationExpressionSyntax parentInvocation)
            {
                current = parentInvocation;
                continue;
            }

            if (current.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Parent is InvocationExpressionSyntax parentCall)
            {
                current = parentCall;
                continue;
            }

            break;
        }

        return current;
    }

    private static bool IsLikelyQueryChain(InvocationExpressionSyntax invocation)
    {
        var methodNames = GetInvocationChainMethodNames(invocation).ToArray();

        if (methodNames.Length == 0)
        {
            return false;
        }

        return methodNames.Any(name => TerminalMethods.Contains(name) || QueryChainMethods.Contains(name));
    }

    private static IEnumerable<string> GetInvocationChainMethodNames(InvocationExpressionSyntax invocation)
    {
        SyntaxNode? current = invocation;

        while (current is InvocationExpressionSyntax currentInvocation)
        {
            if (currentInvocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                yield break;
            }

            yield return memberAccess.Name.Identifier.Text;

            current = memberAccess.Expression;
        }
    }

    private static bool LooksLikeDbContextRoot(string? rootName)
    {
        if (string.IsNullOrWhiteSpace(rootName))
        {
            return false;
        }

        if (string.Equals(rootName, "db", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rootName, "_db", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rootName, "context", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rootName, "dbContext", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rootName, "_dbContext", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return rootName.Contains("context", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStaticTypeRoot(string? rootName)
    {
        if (string.IsNullOrWhiteSpace(rootName))
        {
            return false;
        }

        return char.IsUpper(rootName[0]);
    }
}
