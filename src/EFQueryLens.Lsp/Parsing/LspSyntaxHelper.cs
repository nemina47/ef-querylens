using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public sealed record SourceUsingContext(
    IReadOnlyList<string> Imports,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<string> StaticTypes);

public sealed record LinqChainInfo(
    string Expression,
    string ContextVariableName,
    string DbSetMemberName,
    int Line,
    int Character);

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

    private static readonly HashSet<string> TakeOneTerminalMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Any", "AnyAsync",
        "First", "FirstOrDefault", "FirstAsync", "FirstOrDefaultAsync"
    };

    private static readonly HashSet<string> TakeTwoTerminalMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Single", "SingleOrDefault", "SingleAsync", "SingleOrDefaultAsync"
    };

    private static readonly HashSet<string> CountTerminalMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Count", "CountAsync", "LongCount", "LongCountAsync"
    };

    private static readonly HashSet<string> QueryChainMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Where", "Select", "SelectMany", "Join", "GroupBy", "OrderBy", "OrderByDescending",
        "ThenBy", "ThenByDescending", "Skip", "Take", "Distinct", "Include", "ThenInclude",
        "AsNoTracking", "AsTracking", "AsSplitQuery", "AsSingleQuery", "Expressionify"
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
            if (TryRewriteTerminalInvocation(
                    terminalAccess.Expression,
                    terminalAccess.Name.Identifier.Text,
                    terminalInvocation.ArgumentList.Arguments,
                    terminalInvocation,
                    out var rewritten))
            {
                targetExpression = rewritten;
                continue;
            }

            targetExpression = terminalAccess.Expression;
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

    public static IReadOnlyList<LinqChainInfo> FindAllLinqChains(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetRoot();

        var results = new List<LinqChainInfo>();
        var seenSpans = new HashSet<string>(StringComparer.Ordinal);

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // Ignore nested queries inside lambda selectors/predicates. The outer query
            // already captures the SQL that EF Core will generate.
            if (IsInsideLambda(invocation))
            {
                continue;
            }

            var outermostInvocation = GetOutermostInvocationChain(invocation);
            if (!IsLikelyQueryChain(outermostInvocation))
            {
                continue;
            }

            var outermostSpan = outermostInvocation.Span;
            var outermostSpanKey = $"{outermostSpan.Start}:{outermostSpan.Length}";
            if (!seenSpans.Add(outermostSpanKey))
            {
                continue;
            }

            var targetExpression = StripTerminalInvocation(outermostInvocation) ?? outermostInvocation;
            if (targetExpression is null)
            {
                continue;
            }

            targetExpression = TryInlineLocalQueryRoot(targetExpression, outermostInvocation);
            targetExpression = StripTransparentQueryableCasts(targetExpression);

            var contextVariableName = TryExtractRootContextVariable(targetExpression)
                ?? targetExpression.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Select(i => i.Identifier.Text)
                    .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(contextVariableName))
            {
                continue;
            }

            if (!TryExtractFirstMemberAfterRoot(targetExpression, out var dbSetMemberName)
                || string.IsNullOrWhiteSpace(dbSetMemberName))
            {
                continue;
            }

            var start = tree.GetLineSpan(targetExpression.Span).StartLinePosition;
            results.Add(new LinqChainInfo(
                targetExpression.ToString(),
                contextVariableName,
                dbSetMemberName,
                start.Line,
                start.Character));
        }

        return results
            .OrderBy(r => r.Line)
            .ThenBy(r => r.Character)
            .ToArray();
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

    private static bool TryGetTerminalMethodName(InvocationExpressionSyntax invocation, out string methodName)
    {
        methodName = string.Empty;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        methodName = memberAccess.Name.Identifier.Text;
        return TerminalMethods.Contains(methodName);
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
        var methodNames = invocation.DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Select(ma => ma.Name.Identifier.Text)
            .ToArray();

        if (methodNames.Length == 0)
        {
            return false;
        }

        return methodNames.Any(name => TerminalMethods.Contains(name) || QueryChainMethods.Contains(name));
    }

    private static ExpressionSyntax? StripTerminalInvocation(InvocationExpressionSyntax invocation)
    {
        ExpressionSyntax targetExpression = invocation;

        while (targetExpression is InvocationExpressionSyntax terminalInvocation
               && terminalInvocation.Expression is MemberAccessExpressionSyntax terminalAccess
               && TerminalMethods.Contains(terminalAccess.Name.Identifier.Text))
        {
            if (TryRewriteTerminalInvocation(
                    terminalAccess.Expression,
                    terminalAccess.Name.Identifier.Text,
                    terminalInvocation.ArgumentList.Arguments,
                    terminalInvocation,
                    out var rewritten))
            {
                targetExpression = rewritten;
                continue;
            }

            targetExpression = terminalAccess.Expression;
        }

        return targetExpression;
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

                case CastExpressionSyntax cast:
                    current = cast.Expression;
                    continue;

                default:
                    return false;
            }
        }
    }

    private static bool TryRewriteTerminalInvocation(
        ExpressionSyntax source,
        string terminalMethodName,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        InvocationExpressionSyntax invocationContext,
        out ExpressionSyntax rewritten)
    {
        var query = source;

        if (query is IdentifierNameSyntax queryIdentifier
            && TryResolveLocalExpression(queryIdentifier.Identifier.ValueText, invocationContext, out var resolvedQuery))
        {
            query = resolvedQuery;
        }

        if (TryExtractPredicateArgument(terminalMethodName, arguments, invocationContext, out var predicateArgument))
        {
            query = CreateWhereCall(query, predicateArgument);
        }

        if (CountTerminalMethods.Contains(terminalMethodName))
        {
            var isLongCount = terminalMethodName.StartsWith("LongCount", StringComparison.OrdinalIgnoreCase);
            rewritten = CreateCountProjectionCall(query, isLongCount);
            return true;
        }

        if (TakeOneTerminalMethods.Contains(terminalMethodName))
        {
            rewritten = CreateTakeCall(query, 1);
            return true;
        }

        if (TakeTwoTerminalMethods.Contains(terminalMethodName))
        {
            rewritten = CreateTakeCall(query, 2);
            return true;
        }

        if (PredicateTerminalMethods.Contains(terminalMethodName))
        {
            rewritten = query;
            return true;
        }

        rewritten = source;
        return false;
    }

    private static bool TryExtractPredicateArgument(
        string terminalMethodName,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        InvocationExpressionSyntax invocationContext,
        out ExpressionSyntax predicateArgument)
    {
        predicateArgument = null!;

        if (!PredicateTerminalMethods.Contains(terminalMethodName) || arguments.Count == 0)
            return false;

        foreach (var argument in arguments)
        {
            if (IsCancellationTokenArgument(argument))
                continue;

            if (argument.Expression is LambdaExpressionSyntax
                || argument.Expression is AnonymousMethodExpressionSyntax
                || argument.Expression is MemberAccessExpressionSyntax)
            {
                predicateArgument = argument.Expression;
                return true;
            }

            if (argument.Expression is IdentifierNameSyntax identifier)
            {
                if (TryResolveLocalPredicateExpression(identifier.Identifier.ValueText, invocationContext, out var resolvedPredicate))
                {
                    predicateArgument = resolvedPredicate;
                    return true;
                }

                predicateArgument = identifier;
                return true;
            }
        }

        return false;
    }

    private static ExpressionSyntax StripTransparentQueryableCasts(ExpressionSyntax expression)
    {
        var rewritten = new TransparentQueryableCastStripper().Visit(expression) as ExpressionSyntax;
        return rewritten ?? expression;
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

        var anchorStatement = invocationContext.FirstAncestorOrSelf<StatementSyntax>();
        if (anchorStatement is null)
            return false;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        return TryResolveLocalExpressionCore(identifier, anchorStatement, visited, out expression);
    }

    private static ExpressionSyntax TryInlineLocalQueryRoot(
        ExpressionSyntax expression,
        InvocationExpressionSyntax invocationContext)
    {
        if (!TryGetLeftMostExpression(expression, out var leftMostExpression))
            return expression;

        if (leftMostExpression is not IdentifierNameSyntax identifier)
            return expression;

        if (!TryResolveLocalExpression(identifier.Identifier.ValueText, invocationContext, out var resolvedExpression))
            return expression;

        return expression.ReplaceNode(leftMostExpression, resolvedExpression.WithoutTrivia());
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

    private static bool TryResolveLocalExpressionCore(
        string identifier,
        StatementSyntax anchorStatement,
        ISet<string> visited,
        out ExpressionSyntax expression)
    {
        expression = null!;

        if (!visited.Add(identifier))
            return false;

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
                        if (TryResolveLocalExpressionCore(nestedIdentifier.Identifier.ValueText, statement, visited, out expression))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        expression = declaredExpression;
                        return true;
                    }
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

    private static InvocationExpressionSyntax CreateTakeCall(ExpressionSyntax source, int count)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                source,
                SyntaxFactory.IdentifierName("Take")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(count))))));
    }

    private static ExpressionSyntax CreateCountProjectionCall(ExpressionSyntax source, bool useLongCount)
    {
        var countMethod = useLongCount ? "LongCount" : "Count";
        return SyntaxFactory.ParseExpression($"({source}).GroupBy(_ => 1).Select(g => g.{countMethod}())");
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
