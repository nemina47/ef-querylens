using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Core.Scripting.Evaluation;

internal sealed partial class CompilationPipeline
{
    internal static bool TryNormalizeInaccessibleProjectionTypeFromErrors(
        IReadOnlyCollection<Diagnostic> errors,
        string expression,
        out string normalizedExpression)
    {
        normalizedExpression = expression;

        // Private/internal projection DTOs (for example Program.BlogDto) are not visible
        // to the generated eval assembly. Rewrite terminal Select new Type(...) to
        // Select new { ... } so SQL translation can proceed.
        var hasProtectionLevelError = errors.Any(d => d.Id == "CS0122");
        if (!hasProtectionLevelError)
            return false;

        ExpressionSyntax parsed;
        try
        {
            parsed = SyntaxFactory.ParseExpression(expression);
        }
        catch
        {
            return false;
        }

        var rewriter = new InaccessibleProjectionRewriter();
        var rewritten = (ExpressionSyntax?)rewriter.Visit(parsed);
        if (!rewriter.Changed || rewritten is null)
            return false;

        normalizedExpression = rewritten.ToString();
        return !string.Equals(normalizedExpression, expression, StringComparison.Ordinal);
    }

    private sealed class InaccessibleProjectionRewriter : CSharpSyntaxRewriter
    {
        public bool Changed { get; private set; }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node);
            if (visited?.Expression is not MemberAccessExpressionSyntax member
                || member.Name.Identifier.ValueText != "Select"
                || visited.ArgumentList.Arguments.Count != 1)
                return visited;

            var argument = visited.ArgumentList.Arguments[0];
            var rewrittenArgExpression = RewriteProjectionLambda(visited, argument.Expression);
            if (rewrittenArgExpression is null)
                return visited;

            Changed = true;
            var rewrittenArg = argument.WithExpression(rewrittenArgExpression);
            return visited.WithArgumentList(
                visited.ArgumentList.WithArguments(
                    SyntaxFactory.SingletonSeparatedList(rewrittenArg)));
        }

        private static ExpressionSyntax? RewriteProjectionLambda(
            InvocationExpressionSyntax selectInvocation,
            ExpressionSyntax expr)
        {
            switch (expr)
            {
                case SimpleLambdaExpressionSyntax simple when simple.Body is ObjectCreationExpressionSyntax objectCreation:
                {
                    var expectedNames = CollectDownstreamMemberNames(selectInvocation, simple.Parameter.Identifier.ValueText);
                    return simple.WithBody(BuildAnonymousProjection(objectCreation, expectedNames));
                }
                case ParenthesizedLambdaExpressionSyntax paren when paren.Body is ObjectCreationExpressionSyntax objectCreation:
                {
                    var lambdaParam = paren.ParameterList.Parameters.FirstOrDefault()?.Identifier.ValueText;
                    var expectedNames = string.IsNullOrWhiteSpace(lambdaParam)
                        ? []
                        : CollectDownstreamMemberNames(selectInvocation, lambdaParam!);
                    return paren.WithBody(BuildAnonymousProjection(objectCreation, expectedNames));
                }
                default:
                    return null;
            }
        }

        private static AnonymousObjectCreationExpressionSyntax BuildAnonymousProjection(
            ObjectCreationExpressionSyntax objectCreation,
            IReadOnlyList<string> expectedNames)
        {
            if (objectCreation.Initializer is not null)
            {
                var membersFromInitializer = objectCreation.Initializer.Expressions
                    .OfType<AssignmentExpressionSyntax>()
                    .Select(a =>
                    {
                        var memberName = a.Left switch
                        {
                            IdentifierNameSyntax id => id.Identifier.ValueText,
                            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                            _ => null,
                        };

                        return string.IsNullOrWhiteSpace(memberName)
                            ? null
                            : SyntaxFactory.AnonymousObjectMemberDeclarator(
                                SyntaxFactory.NameEquals(memberName),
                                a.Right.WithoutTrivia());
                    })
                    .Where(static m => m is not null)
                    .Cast<AnonymousObjectMemberDeclaratorSyntax>()
                    .ToList();

                if (membersFromInitializer.Count > 0)
                {
                    return SyntaxFactory.AnonymousObjectCreationExpression(
                        SyntaxFactory.SeparatedList(membersFromInitializer));
                }
            }

            var args = objectCreation.ArgumentList?.Arguments ?? [];
            var members = new List<AnonymousObjectMemberDeclaratorSyntax>();

            for (var i = 0; i < args.Count; i++)
            {
                var expression = args[i].Expression.WithoutTrivia();
                var expectedName = i < expectedNames.Count ? expectedNames[i] : null;
                var memberName = !string.IsNullOrWhiteSpace(expectedName)
                    ? expectedName!
                    : TryInferMemberName(expression) ?? $"__ql{i}";
                members.Add(
                    SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.NameEquals(memberName),
                        expression));
            }

            if (members.Count == 0)
            {
                members.Add(
                    SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.NameEquals("__ql0"),
                        SyntaxFactory.CastExpression(
                            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
                            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))));
            }

            return SyntaxFactory.AnonymousObjectCreationExpression(
                SyntaxFactory.SeparatedList(members));
        }

        private static IReadOnlyList<string> CollectDownstreamMemberNames(
            InvocationExpressionSyntax selectInvocation,
            string lambdaParameter)
        {
            // Inspect only the immediately chained invocation (if any), which covers the
            // common pattern: .Select(x => new PrivateDto(...)).Select(x => new { x.A, x.B })
            // and allows us to preserve A/B member names when rewriting the first projection.
            if (selectInvocation.Parent is not MemberAccessExpressionSyntax parentAccess
                || parentAccess.Parent is not InvocationExpressionSyntax chainedInvocation)
            {
                return [];
            }

            return chainedInvocation.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(m => m.Expression is IdentifierNameSyntax id
                            && string.Equals(id.Identifier.ValueText, lambdaParameter, StringComparison.Ordinal))
                .Select(m => m.Name.Identifier.ValueText)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static string? TryInferMemberName(ExpressionSyntax expression) =>
            expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                _ => null,
            };
    }
}
