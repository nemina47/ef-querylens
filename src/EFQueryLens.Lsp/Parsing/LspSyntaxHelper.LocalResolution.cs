using System.Collections.Generic;
using System.Linq;
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
    /// If the expression is a chain wrapped by post-materialization in-memory operations,
    /// returns the DB-evaluable terminal boundary.
    ///
    /// Examples:
    /// - <c>(await query.ToListAsync(ct)).ToList()</c> -> <c>query.ToListAsync(ct)</c>
    /// - <c>query.ToList().DistinctBy(x => x.Id).ToList()</c> -> <c>query.ToList()</c>
    ///
    /// Returns <paramref name="expression"/> unchanged when no clear materialization
    /// boundary is found while walking the outer chain.
    /// </summary>
    private static ExpressionSyntax StripOuterAwaitChain(ExpressionSyntax expression)
    {
        var current = expression;
        for (var depth = 0; depth < 16; depth++)
        {
            if (current is AwaitExpressionSyntax awaitExpr)
            {
                return awaitExpr.Expression;
            }

            if (current is InvocationExpressionSyntax invocation
                && IsMaterializationBoundaryInvocation(invocation))
            {
                return invocation;
            }

            if (TryStepToChainReceiver(current, out var next))
            {
                current = next;
                continue;
            }

            // Reached a node that is not part of the outer chain
            // (e.g. an IdentifierName at the root of a normal query).
            // No top-level await found - return the original expression.
            return expression;
        }

        return expression;
    }

    private static bool IsMaterializationBoundaryInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        var methodName = memberAccess.Name.Identifier.ValueText;
        if (!TerminalMethods.Contains(methodName))
        {
            return false;
        }

        // If an earlier terminal already exists in this outer chain, this invocation
        // is post-materialization in-memory processing and should not become boundary.
        var chainMethodNames = GetInvocationChainMethodNames(invocation).ToArray();
        for (var i = 1; i < chainMethodNames.Length; i++)
        {
            if (TerminalMethods.Contains(chainMethodNames[i]))
            {
                return false;
            }
        }

        var receiver = UnwrapParenthesizedExpression(memberAccess.Expression);
        if (receiver is AwaitExpressionSyntax)
        {
            return false;
        }

        // Receiver should look like an EF/queryable chain. This avoids treating
        // plain in-memory sequences as SQL query boundaries.
        if (!IsLikelyQueryableExpression(receiver))
        {
            return false;
        }

        if (receiver is InvocationExpressionSyntax receiverInvocation)
        {
            return IsLikelyQueryChain(receiverInvocation);
        }

        return true;
    }

    private static ExpressionSyntax UnwrapParenthesizedExpression(ExpressionSyntax expression)
    {
        var current = expression;
        while (current is ParenthesizedExpressionSyntax parenthesized)
        {
            current = parenthesized.Expression;
        }

        return current;
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
        var rootAnchorStatement = anchorStatement;
        var currentExpression = expression;
        var currentAnchorStatement = anchorStatement;

        for (var depth = 0; depth < 32; depth++)
        {
            if (!TryGetLeftMostExpression(currentExpression, out var leftMostExpression)
                || leftMostExpression is not IdentifierNameSyntax identifier)
            {
                if (leftMostExpression is QueryExpressionSyntax queryExpression
                    && queryExpression.FromClause.Expression is IdentifierNameSyntax fromIdentifier)
                {
                    var fromIdentifierName = fromIdentifier.Identifier.ValueText;
                    if (!string.IsNullOrWhiteSpace(fromIdentifierName))
                    {
                        if (depth == 0
                            && TryCollectQueryVariableFlow(
                                fromIdentifierName,
                                currentAnchorStatement,
                                out var queryFlowExpression,
                                out var queryFlowBaseStatement)
                            && queryFlowBaseStatement is not null
                            && IsLikelyQueryableExpression(queryFlowExpression))
                        {
                            var replacementExpression = PrepareInlinedRootExpression(queryFlowExpression);
                            var replacedQuery = queryExpression.WithFromClause(
                                queryExpression.FromClause.WithExpression(replacementExpression));
                            currentExpression = currentExpression.ReplaceNode(leftMostExpression, replacedQuery.WithoutTrivia());
                            currentAnchorStatement = queryFlowBaseStatement;
                            continue;
                        }

                        if (TryResolveLocalExpressionCore(
                                fromIdentifierName,
                                currentAnchorStatement,
                                out var resolvedQueryExpression,
                                out var resolvedQueryAtStatement)
                            && resolvedQueryAtStatement is not null
                            && IsLikelyQueryableExpression(resolvedQueryExpression))
                        {
                            var replacementExpression = PrepareInlinedRootExpression(resolvedQueryExpression);
                            var replacedQuery = queryExpression.WithFromClause(
                                queryExpression.FromClause.WithExpression(replacementExpression));
                            currentExpression = currentExpression.ReplaceNode(leftMostExpression, replacedQuery.WithoutTrivia());
                            currentAnchorStatement = resolvedQueryAtStatement;
                            continue;
                        }
                    }
                }

                break;
            }

            if (depth == 0
                && TryCollectQueryVariableFlow(
                    identifier.Identifier.ValueText,
                    currentAnchorStatement,
                    out var flowExpression,
                    out var flowBaseStatement)
                && flowBaseStatement is not null)
            {
                var replacementExpression = PrepareInlinedRootExpression(flowExpression);
                currentExpression = currentExpression.ReplaceNode(leftMostExpression, replacementExpression);
                currentAnchorStatement = flowBaseStatement;
                continue;
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

            var replacement = PrepareInlinedRootExpression(resolvedExpression);
            currentExpression = currentExpression.ReplaceNode(leftMostExpression, replacement);
            currentAnchorStatement = resolvedAtStatement;
        }

        // Resolve set-operation argument identifiers (Concat/Union/Except/Intersect)
        // against the original hover statement scope, not the most recently resolved
        // left-most declaration statement.
        currentExpression = InlineSetOperationIdentifierArguments(currentExpression, rootAnchorStatement);
        return currentExpression;
    }

    private static ExpressionSyntax PrepareInlinedRootExpression(ExpressionSyntax expression)
    {
        var replacement = expression.WithoutTrivia();
        // Preserve semantics when an awaited DbContext factory result is used as
        // the receiver of a member-access chain: (await factory.Create...).DbSet.
        return replacement is AwaitExpressionSyntax
            ? SyntaxFactory.ParenthesizedExpression(replacement)
            : replacement;
    }

    private static ExpressionSyntax InlineSetOperationIdentifierArguments(
        ExpressionSyntax expression,
        StatementSyntax anchorStatement)
    {
        var rewritten = new SetOperationArgumentInliner(anchorStatement).Visit(expression) as ExpressionSyntax;
        return rewritten ?? expression;
    }

    private static bool IsSetOperationInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        return memberAccess.Name.Identifier.ValueText is "Concat" or "Union" or "Except" or "Intersect";
    }

    private static bool IsLikelyQueryableExpression(ExpressionSyntax expression)
    {
        return expression switch
        {
            InvocationExpressionSyntax invocation => IsLikelyQueryChain(GetOutermostInvocationChain(invocation)),
            QueryExpressionSyntax => true,
            MemberAccessExpressionSyntax => true,
            _ => false,
        };
    }

    private static bool TryGetLeftMostExpression(ExpressionSyntax expression, out ExpressionSyntax leftMost)
    {
        leftMost = expression;
        var current = expression;

        while (true)
        {
            if (current is CastExpressionSyntax cast)
            {
                current = cast.Expression;
                continue;
            }

            if (TryStepToChainReceiver(current, out var next))
            {
                current = next;
                continue;
            }

            leftMost = current;
            return true;
        }
    }

    private static bool TryStepToChainReceiver(ExpressionSyntax current, out ExpressionSyntax next)
    {
        switch (current)
        {
            case InvocationExpressionSyntax invocation
                when invocation.Expression is MemberAccessExpressionSyntax memberAccess:
                next = memberAccess.Expression;
                return true;

            case MemberAccessExpressionSyntax member:
                next = member.Expression;
                return true;

            case ParenthesizedExpressionSyntax parenthesized:
                next = parenthesized.Expression;
                return true;

            default:
                next = current;
                return false;
        }
    }

    private static bool TryResolveLocalExpressionCore(
        string identifier,
        StatementSyntax anchorStatement,
        out ExpressionSyntax expression,
        out StatementSyntax? resolvedAtStatement)
    {
        expression = null!;
        resolvedAtStatement = null;

        for (SyntaxNode? scope = anchorStatement.Parent; scope is not null; scope = scope.Parent)
        {
            if (!TryGetStatementContainer(scope, out var statements))
                continue;

            var anchorIndex = statements.FindIndex(s => ReferenceEquals(s, anchorStatement));
            if (anchorIndex < 0)
                continue;

            for (var i = anchorIndex - 1; i >= 0; i--)
            {
                var statement = statements[i];
                if (TryGetDeclaredExpression(statement, identifier, out var declaredExpression))
                {
                    if (declaredExpression is IdentifierNameSyntax nestedIdentifier)
                    {
                        if (TryResolveLocalExpressionCore(
                                nestedIdentifier.Identifier.ValueText,
                                statement,
                                out expression,
                                out resolvedAtStatement))
                        {
                            return true;
                        }

                        continue;
                    }

                    expression = declaredExpression;
                    resolvedAtStatement = statement;
                    return true;
                }
            }

            var outerStatement = scope.Parent?.FirstAncestorOrSelf<StatementSyntax>();
            if (outerStatement is null || ReferenceEquals(outerStatement, anchorStatement))
                break;

            anchorStatement = outerStatement;
        }

        return false;
    }

    private static bool TryGetStatementContainer(SyntaxNode scope, out List<StatementSyntax> statements)
    {
        switch (scope)
        {
            case BlockSyntax block:
                statements = block.Statements.ToList();
                return true;

            case SwitchSectionSyntax section:
                statements = section.Statements.ToList();
                return true;

            case CompilationUnitSyntax compilationUnit:
                statements = compilationUnit.Members
                    .OfType<GlobalStatementSyntax>()
                    .Select(m => m.Statement)
                    .ToList();
                return statements.Count > 0;

            default:
                statements = null!;
                return false;
        }
    }

    private static bool TryGetDeclaredExpression(
        StatementSyntax statement,
        string identifier,
        out ExpressionSyntax expression)
    {
        expression = null!;

        if (statement is LocalDeclarationStatementSyntax localDeclaration)
        {
            foreach (var variable in localDeclaration.Declaration.Variables)
            {
                if (!string.Equals(variable.Identifier.ValueText, identifier, StringComparison.Ordinal))
                    continue;

                if (variable.Initializer?.Value is not null)
                {
                    expression = variable.Initializer.Value;
                    return true;
                }
            }
        }

        if (statement is ExpressionStatementSyntax expressionStatement
            && expressionStatement.Expression is AssignmentExpressionSyntax assignment
            && assignment.Left is IdentifierNameSyntax leftIdentifier
            && string.Equals(leftIdentifier.Identifier.ValueText, identifier, StringComparison.Ordinal))
        {
            expression = assignment.Right;
            return true;
        }

        return false;
    }

    private static bool TryUnwrapPredicateExpression(ExpressionSyntax expression, out ExpressionSyntax predicate)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return TryUnwrapPredicateExpression(parenthesized.Expression, out predicate);

            case CastExpressionSyntax cast:
                return TryUnwrapPredicateExpression(cast.Expression, out predicate);

            case LambdaExpressionSyntax:
            case AnonymousMethodExpressionSyntax:
                predicate = expression;
                return true;

            default:
                predicate = null!;
                return false;
        }
    }

    private static bool TryResolveLocalPredicateExpression(
        string identifier,
        InvocationExpressionSyntax invocationContext,
        out ExpressionSyntax predicate)
    {
        predicate = null!;

        if (!TryResolveLocalExpression(identifier, invocationContext, out var expression))
            return false;

        return TryUnwrapPredicateExpression(expression, out predicate);
    }

    private static bool TryResolveLocalExpression(
        string identifier,
        InvocationExpressionSyntax invocationContext,
        out ExpressionSyntax expression)
    {
        expression = null!;

        if (!TryResolveLocalExpression(identifier, invocationContext, out expression, out var resolvedAtStatement)
            || resolvedAtStatement is null)
        {
            return false;
        }

        expression = InlineLeftMostIdentifierChain(expression, resolvedAtStatement);
        return true;
    }

    private static bool TryResolveLocalExpression(
        string identifier,
        InvocationExpressionSyntax invocationContext,
        out ExpressionSyntax expression,
        out StatementSyntax? resolvedAtStatement)
    {
        expression = null!;
        resolvedAtStatement = null;

        var anchorStatement = invocationContext.FirstAncestorOrSelf<StatementSyntax>();
        if (anchorStatement is null)
            return false;

        return TryResolveLocalExpressionCore(identifier, anchorStatement, out expression, out resolvedAtStatement);
    }

    private sealed class SetOperationArgumentInliner : CSharpSyntaxRewriter
    {
        private readonly StatementSyntax _anchorStatement;

        public SetOperationArgumentInliner(StatementSyntax anchorStatement)
        {
            _anchorStatement = anchorStatement;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (!IsSetOperationInvocation(visited))
            {
                return visited;
            }

            var changed = false;
            var arguments = new List<ArgumentSyntax>(visited.ArgumentList.Arguments.Count);

            foreach (var argument in visited.ArgumentList.Arguments)
            {
                if (argument.Expression is IdentifierNameSyntax identifier
                    && TryResolveLocalExpressionCore(
                        identifier.Identifier.ValueText,
                        _anchorStatement,
                        out var resolvedExpression,
                        out _)
                    && IsLikelyQueryableExpression(resolvedExpression))
                {
                    arguments.Add(argument.WithExpression(resolvedExpression.WithoutTrivia()));
                    changed = true;
                    continue;
                }

                arguments.Add(argument);
            }

            if (!changed)
            {
                return visited;
            }

            return visited.WithArgumentList(
                visited.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(arguments)));
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
