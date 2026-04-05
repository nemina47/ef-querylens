using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    /// <summary>
    /// Applies syntax-only normalizations to a freshly-extracted LINQ expression before
    /// it is sent to the daemon for translation.
    ///
    /// These cover patterns that EF Core's expression-tree compiler cannot translate and
    /// that can be rewritten safely from syntax alone, without runtime model access:
    ///
    ///   - <c>x is null</c> / <c>x is Constant</c> pattern expressions become plain
    ///     equality comparisons (<c>x == null</c>, <c>x == Constant</c>).
    ///   - Ternary conditions that compare a pattern-match with equality (e.g.
    ///     <c>(condition is true) == value ? a : b</c>) are unwrapped to a direct
    ///     conditional on the pattern expression.
    ///
    /// Normalizations are applied unconditionally; if an expression does not match a
    /// pattern the rewriter returns it unchanged.  Callers may pass any expression string;
    /// null or whitespace is returned as-is.
    /// </summary>
    internal static string PreNormalizeExtractedExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var result = expression;
        result = ApplyCollectionExpressionReceiverNormalization(result);
        result = ApplyPatternMatchingNormalization(result);
        result = ApplyTernaryPatternNormalization(result);
        result = ApplySetOperationProjectionNormalization(result);
        return result;
    }

    private static string ApplyCollectionExpressionReceiverNormalization(string expression)
    {
        if (!expression.Contains('[', StringComparison.Ordinal)
            || !expression.Contains(']', StringComparison.Ordinal))
        {
            return expression;
        }

        ExpressionSyntax parsed;
        try { parsed = SyntaxFactory.ParseExpression(expression); }
        catch { return expression; }

        var rewriter = new LspCollectionExpressionReceiverRewriter();
        var rewritten = rewriter.Visit(parsed) as ExpressionSyntax;
        return rewriter.Changed && rewritten is not null
            ? rewritten.NormalizeWhitespace().ToString()
            : expression;
    }

    private static string ApplyPatternMatchingNormalization(string expression)
    {
        if (!expression.Contains(" is ", StringComparison.Ordinal))
            return expression;

        ExpressionSyntax parsed;
        try { parsed = SyntaxFactory.ParseExpression(expression); }
        catch { return expression; }

        var rewriter = new LspPatternExpressionTreeRewriter();
        var rewritten = rewriter.Visit(parsed) as ExpressionSyntax;
        return rewriter.Changed && rewritten is not null
            ? rewritten.ToString()
            : expression;
    }

    private static string ApplyTernaryPatternNormalization(string expression)
    {
        if (!expression.Contains('?') || !expression.Contains(':'))
            return expression;

        ExpressionSyntax parsed;
        try { parsed = SyntaxFactory.ParseExpression(expression); }
        catch { return expression; }

        var rewriter = new LspPatternTernaryComparisonRewriter();
        var rewritten = rewriter.Visit(parsed) as ExpressionSyntax;
        return rewriter.Changed && rewritten is not null
            ? rewritten.ToString()
            : expression;
    }

    private static string ApplySetOperationProjectionNormalization(string expression)
    {
        if (!expression.Contains(".Concat(", StringComparison.Ordinal)
            && !expression.Contains(".Union(", StringComparison.Ordinal))
        {
            return expression;
        }

        ExpressionSyntax parsed;
        try { parsed = ParseExpression(expression); }
        catch { return expression; }

        var rewriter = new LspSetOperationProjectionRewriter();
        var rewritten = rewriter.Visit(parsed) as ExpressionSyntax;
        return rewriter.Changed && rewritten is not null
            ? rewritten.NormalizeWhitespace().ToString()
            : expression;
    }

    // ── Rewriters ────────────────────────────────────────────────────────────

    private sealed class LspPatternExpressionTreeRewriter : CSharpSyntaxRewriter
    {
        public bool Changed { get; private set; }

        public override SyntaxNode VisitIsPatternExpression(IsPatternExpressionSyntax node)
        {
            var visited = (IsPatternExpressionSyntax)base.VisitIsPatternExpression(node)!;

            if (!TryConvertPatternToBooleanExpression(visited.Expression, visited.Pattern, out var converted))
                return visited;

            Changed = true;
            return converted.WithTriviaFrom(node);
        }

        private static bool TryConvertPatternToBooleanExpression(
            ExpressionSyntax target,
            PatternSyntax pattern,
            out ExpressionSyntax converted)
        {
            switch (pattern)
            {
                case ConstantPatternSyntax constantPattern:
                    converted = SyntaxFactory.BinaryExpression(
                        SyntaxKind.EqualsExpression,
                        ParenthesizeIfNeeded(target),
                        SpacedToken(SyntaxKind.EqualsEqualsToken),
                        ParenthesizeIfNeeded(constantPattern.Expression));
                    return true;

                case ParenthesizedPatternSyntax parenthesizedPattern:
                    return TryConvertPatternToBooleanExpression(
                        target,
                        parenthesizedPattern.Pattern,
                        out converted);

                case BinaryPatternSyntax binaryPattern:
                    if (!TryConvertPatternToBooleanExpression(target, binaryPattern.Left, out var left)
                        || !TryConvertPatternToBooleanExpression(target, binaryPattern.Right, out var right))
                    {
                        break;
                    }

                    var kind = binaryPattern.IsKind(SyntaxKind.OrPattern)
                        ? SyntaxKind.LogicalOrExpression
                        : binaryPattern.IsKind(SyntaxKind.AndPattern)
                            ? SyntaxKind.LogicalAndExpression
                            : SyntaxKind.None;

                    if (kind == SyntaxKind.None)
                        break;

                    var opToken = kind == SyntaxKind.LogicalOrExpression
                        ? SpacedToken(SyntaxKind.BarBarToken)
                        : SpacedToken(SyntaxKind.AmpersandAmpersandToken);
                    converted = SyntaxFactory.BinaryExpression(
                        kind,
                        ParenthesizeIfNeeded(left),
                        opToken,
                        ParenthesizeIfNeeded(right));
                    return true;
            }

            converted = null!;
            return false;
        }

        private static SyntaxToken SpacedToken(SyntaxKind kind) =>
            SyntaxFactory.Token(kind)
                .WithLeadingTrivia(SyntaxFactory.Space)
                .WithTrailingTrivia(SyntaxFactory.Space);

        private static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expression) =>
            expression is ParenthesizedExpressionSyntax
                or IdentifierNameSyntax
                or MemberAccessExpressionSyntax
                or LiteralExpressionSyntax
                ? expression
                : SyntaxFactory.ParenthesizedExpression(expression);
    }

    private sealed class LspPatternTernaryComparisonRewriter : CSharpSyntaxRewriter
    {
        public bool Changed { get; private set; }

        public override SyntaxNode VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            var visited = (ConditionalExpressionSyntax)base.VisitConditionalExpression(node)!;

            var candidates = visited.Condition
                .DescendantNodesAndSelf()
                .OfType<BinaryExpressionSyntax>()
                .Where(b =>
                    b.IsKind(SyntaxKind.EqualsExpression)
                    || b.IsKind(SyntaxKind.NotEqualsExpression))
                .Select(b =>
                {
                    if (b.Right is IsPatternExpressionSyntax rightPattern)
                        return new { Comparison = b, Pattern = rightPattern, CompareValue = b.Left };
                    if (b.Left is IsPatternExpressionSyntax leftPattern)
                        return new { Comparison = b, Pattern = leftPattern, CompareValue = b.Right };
                    return null;
                })
                .Where(x => x is not null)
                .OrderByDescending(x => x!.Comparison.SpanStart)
                .ToList();

            if (candidates.Count == 0)
                return visited;

            var selected = candidates[0]!;
            var rhsConditional = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.ConditionalExpression(
                    selected.Pattern,
                    visited.WhenTrue,
                    visited.WhenFalse));

            var replacementComparison = SyntaxFactory.BinaryExpression(
                selected.Comparison.Kind(),
                selected.CompareValue,
                rhsConditional);

            var replacedCondition = visited.Condition.ReplaceNode(
                selected.Comparison,
                replacementComparison);

            Changed = true;
            return replacedCondition.WithTriviaFrom(node);
        }
    }

    private sealed class LspSetOperationProjectionRewriter : CSharpSyntaxRewriter
    {
        public bool Changed { get; private set; }

        public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

            if (!TryRewriteSetOperationProjection(visited, out var rewritten))
            {
                return visited;
            }

            Changed = true;
            return rewritten.WithTriviaFrom(node);
        }

        private static bool TryRewriteSetOperationProjection(
            InvocationExpressionSyntax invocation,
            out InvocationExpressionSyntax rewritten)
        {
            rewritten = null!;

            if (invocation.Expression is not MemberAccessExpressionSyntax setOperationAccess)
                return false;

            var methodName = setOperationAccess.Name.Identifier.ValueText;
            if (!string.Equals(methodName, "Concat", StringComparison.Ordinal)
                && !string.Equals(methodName, "Union", StringComparison.Ordinal))
            {
                return false;
            }

            if (invocation.ArgumentList.Arguments.Count != 1)
                return false;

            if (!TryMatchSelectInvocation(setOperationAccess.Expression, out var leftSource, out var leftSelector))
                return false;

            if (!TryMatchSelectInvocation(invocation.ArgumentList.Arguments[0].Expression, out var rightSource, out var rightSelector))
                return false;

            if (!AreEquivalentSelectors(leftSelector, rightSelector))
                return false;

            var rewrittenSetOperation = InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    ParenthesizeIfNeeded(leftSource),
                    IdentifierName(methodName)),
                ArgumentList(SingletonSeparatedList(Argument(rightSource))));

            rewritten = InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    ParenthesizeIfNeeded(rewrittenSetOperation),
                    IdentifierName("Select")),
                ArgumentList(SingletonSeparatedList(Argument(leftSelector))));
            return true;
        }

        private static bool TryMatchSelectInvocation(
            ExpressionSyntax expression,
            out ExpressionSyntax source,
            out LambdaExpressionSyntax selector)
        {
            source = null!;
            selector = null!;

            expression = UnwrapParentheses(expression);
            if (expression is not InvocationExpressionSyntax invocation
                || invocation.Expression is not MemberAccessExpressionSyntax memberAccess
                || !string.Equals(memberAccess.Name.Identifier.ValueText, "Select", StringComparison.Ordinal)
                || invocation.ArgumentList.Arguments.Count != 1)
            {
                return false;
            }

            if (invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda)
                return false;

            source = UnwrapParentheses(memberAccess.Expression);
            selector = lambda;
            return true;
        }

        private static bool AreEquivalentSelectors(LambdaExpressionSyntax left, LambdaExpressionSyntax right)
        {
            if (!TryGetSingleParameter(left, out var leftParameter)
                || !TryGetSingleParameter(right, out var rightParameter))
            {
                return false;
            }

            var leftBody = CanonicalizeLambdaBody(left.Body, leftParameter);
            var rightBody = CanonicalizeLambdaBody(right.Body, rightParameter);
            return string.Equals(leftBody, rightBody, StringComparison.Ordinal);
        }

        private static bool TryGetSingleParameter(LambdaExpressionSyntax lambda, out string parameterName)
        {
            switch (lambda)
            {
                case SimpleLambdaExpressionSyntax simple:
                    parameterName = simple.Parameter.Identifier.ValueText;
                    return true;
                case ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized:
                    parameterName = parenthesized.ParameterList.Parameters[0].Identifier.ValueText;
                    return true;
                default:
                    parameterName = string.Empty;
                    return false;
            }
        }

        private static string CanonicalizeLambdaBody(CSharpSyntaxNode body, string parameterName)
        {
            var rewritten = new LambdaParameterCanonicalizer(parameterName).Visit(body) ?? body;
            return rewritten.NormalizeWhitespace().ToString();
        }

        private static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }

            return expression;
        }

        private static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expression) =>
            expression is IdentifierNameSyntax
                or MemberAccessExpressionSyntax
                or InvocationExpressionSyntax
                or ParenthesizedExpressionSyntax
                ? expression
                : ParenthesizedExpression(expression);

        private sealed class LambdaParameterCanonicalizer : CSharpSyntaxRewriter
        {
            private readonly string _parameterName;

            public LambdaParameterCanonicalizer(string parameterName)
            {
                _parameterName = parameterName;
            }

            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                return string.Equals(node.Identifier.ValueText, _parameterName, StringComparison.Ordinal)
                    ? IdentifierName("__ql_param")
                    : base.VisitIdentifierName(node)!;
            }
        }
    }

    private sealed class LspCollectionExpressionReceiverRewriter : CSharpSyntaxRewriter
    {
        public bool Changed { get; private set; }

        public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
            if (visited.Expression is not CollectionExpressionSyntax collection)
                return visited;

            if (!TryRewriteCollectionExpressionReceiver(collection, out var rewrittenReceiver))
                return visited;

            Changed = true;
            return visited.WithExpression(rewrittenReceiver.WithTriviaFrom(collection));
        }

        private static bool TryRewriteCollectionExpressionReceiver(
            CollectionExpressionSyntax collection,
            out ExpressionSyntax rewrittenReceiver)
        {
            rewrittenReceiver = null!;

            // C# collection expressions with spread can fail in eval compilation when used as
            // method receivers (CS9176: no target type). For the common receiver shape
            // `[.. source].Contains(x)`, rewrite to `source.Contains(x)`.
            if (collection.Elements.Count == 1
                && collection.Elements[0] is SpreadElementSyntax spread)
            {
                rewrittenReceiver = ParenthesizeIfNeeded(spread.Expression.WithoutTrivia());
                return true;
            }

            var elements = new List<ExpressionSyntax>();
            foreach (var element in collection.Elements)
            {
                if (element is not ExpressionElementSyntax expressionElement)
                    return false;

                elements.Add(expressionElement.Expression);
            }

            var initializer = InitializerExpression(
                SyntaxKind.ArrayInitializerExpression,
                SeparatedList(elements));

            rewrittenReceiver = ImplicitArrayCreationExpression(initializer);
            return true;
        }

        private static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expression) =>
            expression is IdentifierNameSyntax
                or MemberAccessExpressionSyntax
                or InvocationExpressionSyntax
                or ParenthesizedExpressionSyntax
                ? expression
                : ParenthesizedExpression(expression);
    }
}
