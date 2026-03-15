using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
    private static bool TryNormalizePatternTernaryComparisonFromErrors(
        IReadOnlyList<Diagnostic> errors,
        string expression,
        out string normalizedExpression)
    {
        normalizedExpression = expression;

        var hasBoolComparisonError = errors.Any(d =>
            d.Id == "CS0019"
            && d.GetMessage().Contains("Operator '==' cannot be applied", StringComparison.Ordinal)
            && d.GetMessage().Contains("and 'bool'", StringComparison.Ordinal));

        if (!hasBoolComparisonError || !expression.Contains('?') || !expression.Contains(':'))
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

        var rewriter = new PatternTernaryComparisonRewriter();
        var rewritten = rewriter.Visit(parsed) as ExpressionSyntax;
        if (!rewriter.Changed || rewritten is null)
            return false;

        normalizedExpression = rewritten.ToString();
        return !string.Equals(normalizedExpression, expression, StringComparison.Ordinal);
    }

    private sealed class PatternTernaryComparisonRewriter : CSharpSyntaxRewriter
    {
        public bool Changed { get; private set; }

        public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            var visited = (ConditionalExpressionSyntax)base.VisitConditionalExpression(node)!;

            var candidates = visited.Condition
                .DescendantNodesAndSelf()
                .OfType<BinaryExpressionSyntax>()
                .Where(b => b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression))
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

            var replacedCondition = visited.Condition.ReplaceNode(selected.Comparison, replacementComparison);
            Changed = true;
            return replacedCondition.WithTriviaFrom(node);
        }
    }

    private static bool TryNormalizeUnsupportedPatternMatchingFromErrors(
        IReadOnlyList<Diagnostic> errors,
        string expression,
        out string normalizedExpression)
    {
        normalizedExpression = expression;

        var hasPatternExpressionTreeError = errors.Any(d =>
            d.Id == "CS8122"
            || d.GetMessage().Contains(
                "expression tree may not contain an 'is' pattern-matching operator",
                StringComparison.OrdinalIgnoreCase));

        if (!hasPatternExpressionTreeError || !expression.Contains(" is ", StringComparison.Ordinal))
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

        var rewriter = new PatternExpressionTreeRewriter();
        var rewritten = rewriter.Visit(parsed) as ExpressionSyntax;
        if (!rewriter.Changed || rewritten is null)
            return false;

        normalizedExpression = rewritten.ToString();
        return !string.Equals(normalizedExpression, expression, StringComparison.Ordinal);
    }

    private sealed class PatternExpressionTreeRewriter : CSharpSyntaxRewriter
    {
        public bool Changed { get; private set; }

        public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
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
                        ParenthesizeIfNeeded(constantPattern.Expression));
                    return true;

                case ParenthesizedPatternSyntax parenthesizedPattern:
                    return TryConvertPatternToBooleanExpression(target, parenthesizedPattern.Pattern, out converted);

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

                    converted = SyntaxFactory.BinaryExpression(kind, ParenthesizeIfNeeded(left), ParenthesizeIfNeeded(right));
                    return true;
            }

            converted = null!;
            return false;
        }

        private static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expression)
        {
            if (expression is ParenthesizedExpressionSyntax
                || expression is IdentifierNameSyntax
                || expression is MemberAccessExpressionSyntax
                || expression is LiteralExpressionSyntax)
            {
                return expression;
            }

            return SyntaxFactory.ParenthesizedExpression(expression);
        }
    }
}
