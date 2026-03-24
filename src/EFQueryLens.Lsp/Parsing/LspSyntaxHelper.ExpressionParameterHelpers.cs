using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    private static bool TryExtractFromExpressionParameterHelperCall(
        SyntaxNode root,
        InvocationExpressionSyntax invocation,
        int cursorPosition,
        out string expression,
        out string? contextVariableName)
    {
        expression = string.Empty;
        contextVariableName = null;

        var helperName = GetInvokedMethodName(invocation);
        if (string.IsNullOrWhiteSpace(helperName))
        {
            return false;
        }

        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count == 0)
        {
            return false;
        }

        var candidates = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => string.Equals(m.Identifier.Text, helperName, StringComparison.Ordinal)
                && m.ParameterList.Parameters.Count == arguments.Count)
            .ToArray();

        foreach (var method in candidates)
        {
            var expressionParameterIndexes = GetExpressionParameterIndexes(method.ParameterList.Parameters);
            if (expressionParameterIndexes.Count == 0)
            {
                continue;
            }

            // Keep this path narrow: only trigger helper synthesis when hover/cursor is
            // inside one of the expression arguments (selector/where style parameters).
            var cursorInsideExpressionArgument = expressionParameterIndexes
                .Any(i => i >= 0
                    && i < arguments.Count
                    && arguments[i].Span.Contains(cursorPosition));
            if (!cursorInsideExpressionArgument)
            {
                continue;
            }

            var queryInvocation = TryFindPrimaryQueryInvocation(method);
            if (queryInvocation is null)
            {
                continue;
            }

            ExpressionSyntax queryExpression = queryInvocation;
            queryExpression = TryInlineLocalQueryRoot(queryExpression, queryInvocation);
            queryExpression = StripTransparentQueryableCasts(queryExpression);

            var queryText = queryExpression.ToString();
            if (string.IsNullOrWhiteSpace(queryText))
            {
                continue;
            }

            queryText = SubstituteMethodParametersWithCallArguments(
                queryText,
                method.ParameterList.Parameters,
                arguments);

            ExpressionSyntax parsed;
            try
            {
                parsed = SyntaxFactory.ParseExpression(queryText);
            }
            catch
            {
                continue;
            }

            if (parsed is InvocationExpressionSyntax parsedInvocation
                && !IsLikelyQueryChain(parsedInvocation))
            {
                continue;
            }

            var rootContext = TryExtractRootContextVariable(parsed)
                ?? parsed.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Select(i => i.Identifier.Text)
                    .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(rootContext))
            {
                continue;
            }

            expression = parsed.ToString();
            contextVariableName = rootContext;
            return true;
        }

        return false;
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            _ => null,
        };
    }

    private static List<int> GetExpressionParameterIndexes(SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        var indexes = new List<int>();
        for (var i = 0; i < parameters.Count; i++)
        {
            var typeText = parameters[i].Type?.ToString();
            if (string.IsNullOrWhiteSpace(typeText))
            {
                continue;
            }

            if (typeText.Contains("Expression<", StringComparison.Ordinal))
            {
                indexes.Add(i);
            }
        }

        return indexes;
    }

    private static InvocationExpressionSyntax? TryFindPrimaryQueryInvocation(MethodDeclarationSyntax method)
    {
        var body = method.Body;
        if (body is null)
        {
            return null;
        }

        InvocationExpressionSyntax? best = null;
        var bestSpan = -1;

        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var outermost = GetOutermostInvocationChain(invocation);
            if (!IsLikelyQueryChain(outermost))
            {
                continue;
            }

            var span = outermost.Span.Length;
            if (span > bestSpan)
            {
                bestSpan = span;
                best = outermost;
            }
        }

        return best;
    }

    private static string SubstituteMethodParametersWithCallArguments(
        string queryText,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        var result = queryText;

        for (var i = 0; i < parameters.Count && i < arguments.Count; i++)
        {
            var parameterName = parameters[i].Identifier.Text;
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                continue;
            }

            var argumentText = arguments[i].Expression.ToString();
            if (string.IsNullOrWhiteSpace(argumentText))
            {
                continue;
            }

            result = Regex.Replace(
                result,
                $@"\b{Regex.Escape(parameterName)}\b",
                argumentText);
        }

        return result;
    }
}
