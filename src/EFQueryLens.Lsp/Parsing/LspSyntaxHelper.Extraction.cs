using System.Collections.Generic;
using System.Linq;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    public sealed record LinqExtractionResult(
        string Expression,
        string ContextVariableName,
        string? CallSiteExpression,
        ExtractionOriginSnapshot Origin);

    public static LinqExtractionResult? TryExtractLinqExpressionDetailed(
        string sourceText,
        string filePath,
        int line,
        int character,
        ProjectSourceIndex? sourceIndex = null,
        string? targetAssemblyPath = null,
        bool skipV2Plan = false)
    {
        if (!skipV2Plan
            && TryBuildV2ExtractionPlan(
                sourceText,
                filePath,
                line,
                character,
                out var v2Plan,
                out _,
                sourceIndex,
                targetAssemblyPath)
            && v2Plan is not null)
        {
            return new LinqExtractionResult(
                v2Plan.Expression,
                v2Plan.ContextVariableName,
                v2Plan.CallSiteExpression,
                v2Plan.Origin);
        }

        var expression = TryExtractLinqExpression(
            sourceText,
            line,
            character,
            out var contextVariableName,
            out var callSiteExpression,
            out var extractionOrigin,
            filePath,
            sourceIndex,
            targetAssemblyPath);

        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(contextVariableName))
            return null;

        return new LinqExtractionResult(
            expression,
            contextVariableName!,
            callSiteExpression,
            extractionOrigin is null
                ? new ExtractionOriginSnapshot
                {
                    FilePath = filePath,
                    Line = line,
                    Character = character,
                    EndLine = line,
                    EndCharacter = character,
                    Scope = "hover",
                }
                : extractionOrigin with
                {
                    FilePath = string.IsNullOrWhiteSpace(extractionOrigin.FilePath)
                        ? filePath
                        : extractionOrigin.FilePath,
                });
    }

    public static string? TryExtractLinqExpression(string sourceText, int line, int character,
        out string? contextVariableName,
        out string? callSiteExpression,
        ProjectSourceIndex? sourceIndex = null,
        string? targetAssemblyPath = null)
        => TryExtractLinqExpression(
            sourceText,
            line,
            character,
            out contextVariableName,
            out callSiteExpression,
            out _,
            filePath: null,
            sourceIndex,
            targetAssemblyPath);

    public static string? TryExtractLinqExpression(string sourceText, int line, int character,
        out string? contextVariableName,
        out string? callSiteExpression,
        out ExtractionOriginSnapshot? extractionOrigin,
        string? filePath = null,
        ProjectSourceIndex? sourceIndex = null,
        string? targetAssemblyPath = null)
    {
        contextVariableName = null;
        callSiteExpression = null;
        extractionOrigin = null;

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
        SemanticModel? helperSemanticModel = null;
        if (TryCreateSemanticModel(sourceText, targetAssemblyPath, out _, out _, out var extractedModel))
        {
            helperSemanticModel = extractedModel;
        }

        InvocationExpressionSyntax? invocation = null;
        ExpressionSyntax? targetExpression = null;
        ExpressionSyntax? sourceBackedExtractionNode = null;

        // Conditional expression special-case:
        // - Hovering inside a query subtree in either branch should extract that branch query.
        // - Hovering elsewhere (condition / operator / outside branch subtrees) falls back to
        //   regular outermost extraction, which preserves current default behavior.
        if (!TryExtractConditionalBranchQueryAtPosition(node, position, out targetExpression, out invocation))
        {
            if (!TryExtractSemanticCandidateAtPosition(
                    sourceText,
                    line,
                    character,
                    targetAssemblyPath,
                    out targetExpression,
                    out invocation))
            {
                // Strict mode: when semantic candidate resolution fails, only keep the
                // nearest invocation under cursor so helper-call extraction still works.
                // Do not broaden to generic member/query-expression heuristics.
                invocation = node?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                targetExpression = invocation;
            }
        }
        sourceBackedExtractionNode = targetExpression;

        if (targetExpression is null
            && TryGetRhsExpressionFromDeclarationOrAssignment(node, out var rhsExpression))
        {
            targetExpression = rhsExpression;
            invocation = rhsExpression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            sourceBackedExtractionNode = rhsExpression;
        }

        if (targetExpression == null)
            return null;
        sourceBackedExtractionNode ??= targetExpression;

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
                    helperSemanticModel,
                    out var synthesizedExpression,
                    out var synthesizedContextVariableName,
                    out var helperOrigin,
                    filePath,
                    line,
                    character,
                    sourceIndex))
            {
                contextVariableName = synthesizedContextVariableName;
                callSiteExpression = finalInvocation.ToString();
                extractionOrigin = helperOrigin;
                return synthesizedExpression;
            }

            if (TryExtractFromQueryableReturningHelperCall(
                    root,
                    finalInvocation,
                    position,
                    helperSemanticModel,
                    out synthesizedExpression,
                    out synthesizedContextVariableName,
                    out helperOrigin,
                    filePath,
                    line,
                    character,
                    sourceIndex))
            {
                contextVariableName = synthesizedContextVariableName;
                callSiteExpression = finalInvocation.ToString();
                extractionOrigin = helperOrigin;
                return synthesizedExpression;
            }

            return null;
        }

        // If the outermost chain is chained on the result of an await expression
        // (e.g. "(await query.ToListAsync()).ToList()"), strip the outer in-memory
        // part and keep only the awaited EF query.  The runner template already
        // handles Task<T> via UnwrapTask; keeping the await would cause CS4032
        // in the generated synchronous scaffold.
        targetExpression = StripOuterAwaitChain(targetExpression);

        // Helper-query inlining for query-like chains:
        // orderQueries.BuildRecentOrdersQuery(...).Select(...).ToListAsync(...)
        // should inline BuildRecentOrdersQuery body to root at _db.* so DbContext
        // disambiguation can use actual DbSet usage instead of service type roots.
        if (targetExpression is InvocationExpressionSyntax chainInvocation)
        {
            var helperInvocation = node?.AncestorsAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.Span.Contains(position))
                .OrderBy(i => i.Span.Length)
                .FirstOrDefault(i =>
                {
                    var methodName = GetInvokedMethodName(i);
                    return !string.IsNullOrWhiteSpace(methodName)
                        && !QueryChainMethods.Contains(methodName)
                        && !TerminalMethods.Contains(methodName);
                });

            if (helperInvocation is null
                && TryFindQueryableHelperInvocationInChain(chainInvocation, position, out var chainHelperInvocation))
            {
                helperInvocation = chainHelperInvocation;
            }

            if (helperInvocation is not null
                && TryExtractFromQueryableReturningHelperCall(
                root,
                helperInvocation,
                position,
                helperSemanticModel,
                out var helperExpression,
                out var helperContextVariableName,
                out var helperOrigin,
                filePath,
                line,
                character,
                sourceIndex))
            {
                try
                {
                    targetExpression = SyntaxFactory.ParseExpression(helperExpression);
                    invocation = targetExpression as InvocationExpressionSyntax
                        ?? targetExpression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    contextVariableName = helperContextVariableName;
                    callSiteExpression = chainInvocation.ToString();
                    extractionOrigin = helperOrigin;
                }
                catch
                {
                    // Keep original extraction if helper synthesis parse fails.
                }
            }
        }

        // Inline local IQueryable variables for non-terminal chains too, so
        // expressions like auditTrailQuery.ApplyPaging(...).ToListAsync(...) are
        // rooted back to dbContext.* and keep DbContext discovery deterministic.
        if (invocation is not null)
        {
            targetExpression = TryInlineLocalQueryRoot(targetExpression, invocation);
        }

        targetExpression = StripTransparentQueryableCasts(targetExpression);

        // Preserve the source-authored chain for hover display. Later extraction-time
        // rewrites (for example inaccessible projection type normalization) are for
        // execution resilience only and should surface under Executed LINQ instead.
        if (string.IsNullOrWhiteSpace(callSiteExpression))
        {
            callSiteExpression = targetExpression.ToString();
        }

        targetExpression = RewriteInaccessibleProjectionTypes(targetExpression, helperSemanticModel);

        // Identify the root variable from the left-most chain segment.
        // Using DescendantNodes().FirstOrDefault() can pick lambda identifiers
        // (e.g. "s") depending on cursor position and trivia layout.
        contextVariableName = TryExtractRootContextVariable(targetExpression)
            ?? targetExpression.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Select(i => i.Identifier.Text)
                .FirstOrDefault();

        // Apply syntax-only normalizations before sending to daemon so the daemon
        // compile-retry loop sees a clean expression.  This is safe to do here
        // because these rewrites are purely syntactic and do not require runtime model
        // or assembly information.
        if (extractionOrigin is null)
        {
            extractionOrigin = BuildExtractionOriginSnapshot(
                sourceBackedExtractionNode,
                "hover-query",
                fallbackFilePath: filePath,
                fallbackLine: line,
                fallbackCharacter: character);
        }

        var candidateExpression = targetExpression.ToString();
        return PreNormalizeExtractedExpression(candidateExpression);
    }

    private static ExpressionSyntax RewriteInaccessibleProjectionTypes(
        ExpressionSyntax expression,
        SemanticModel? semanticModel)
    {
        if (semanticModel is null)
            return expression;

        var rewriter = new InaccessibleProjectionTypeRewriter(semanticModel);
        return rewriter.Visit(expression) as ExpressionSyntax ?? expression;
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

    private static bool TryExtractSemanticCandidateAtPosition(
        string sourceText,
        int line,
        int character,
        string? targetAssemblyPath,
        out ExpressionSyntax? expression,
        out InvocationExpressionSyntax? invocation)
    {
        expression = null;
        invocation = null;

        try
        {
            TryCreateSemanticModel(sourceText, targetAssemblyPath, out var tree, out var root, out var model);
            var textLines = tree.GetText().Lines;
            if (line < 0 || line >= textLines.Count)
                return false;

            var charOffset = Math.Min(Math.Max(character, 0), textLines[line].End - textLines[line].Start);
            var position = textLines[line].Start + charOffset;
            var node = root.FindToken(position).Parent;
            if (node is null)
                return false;

            var semanticInvocations = node.AncestorsAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Select(GetOutermostInvocationChain)
                .GroupBy(static i => (i.SpanStart, i.Span.Length))
                .Select(static g => g.First())
                .OrderBy(static i => i.Span.Length);

            foreach (var candidate in semanticInvocations)
            {
                if (!IsSemanticallyQueryableInvocation(candidate, model))
                    continue;

                expression = candidate;
                invocation = candidate;
                return true;
            }

            var queryExpression = node.FirstAncestorOrSelf<QueryExpressionSyntax>();
            if (queryExpression is not null && IsSemanticallyQueryableExpression(queryExpression, model))
            {
                expression = queryExpression;
                invocation = queryExpression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                return true;
            }

            var semanticMemberAccess = node.AncestorsAndSelf()
                .OfType<MemberAccessExpressionSyntax>()
                .OrderBy(static m => m.Span.Length)
                .FirstOrDefault(member => IsSemanticallyQueryableExpression(member, model));
            if (semanticMemberAccess is not null)
            {
                expression = semanticMemberAccess;
                invocation = semanticMemberAccess.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                return true;
            }
        }
        catch
        {
            // Best-effort semantic short-circuit. Caller falls back to syntax-based extraction.
        }

        return false;
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
                    if (invocation.Expression is IdentifierNameSyntax
                        && invocation.ArgumentList.Arguments.Count > 0)
                    {
                        current = invocation.ArgumentList.Arguments[0].Expression;
                        continue;
                    }

                    current = invocation.Expression;
                    continue;

                case MemberAccessExpressionSyntax memberAccess:
                    lastMemberName = memberAccess.Name.Identifier.Text;
                    current = memberAccess.Expression;
                    continue;

                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case ConditionalExpressionSyntax conditional:
                    current = conditional.WhenTrue;
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
        if (invocation.Expression is MemberAccessExpressionSyntax { Expression: PredefinedTypeSyntax })
        {
            return false;
        }

        var methodNames = GetInvocationChainMethodNames(invocation).ToArray();

        if (methodNames.Length == 0)
        {
            return false;
        }

        return methodNames.Any(name => TerminalMethods.Contains(name) || QueryChainMethods.Contains(name));
    }

    private static bool TryFindQueryableHelperInvocationInChain(
        InvocationExpressionSyntax outerInvocation,
        int cursorPosition,
        out InvocationExpressionSyntax helperInvocation)
    {
        helperInvocation = null!;
        var current = outerInvocation;

        for (var depth = 0; depth < 16; depth++)
        {
            if (current.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                return false;
            }

            var methodName = memberAccess.Name.Identifier.ValueText;
            if (!QueryChainMethods.Contains(methodName)
                && !TerminalMethods.Contains(methodName)
                && current.Span.Contains(cursorPosition))
            {
                helperInvocation = current;
                return true;
            }

            if (memberAccess.Expression is InvocationExpressionSyntax receiverInvocation)
            {
                current = receiverInvocation;
                continue;
            }

            return false;
        }

        return false;
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

    private static bool TryExtractFromExpressionParameterHelperCall(
        SyntaxNode root,
        InvocationExpressionSyntax invocation,
        int cursorPosition,
        SemanticModel? semanticModel,
        out string expression,
        out string? contextVariableName,
        out ExtractionOriginSnapshot? extractionOrigin,
        string? fallbackFilePath = null,
        int fallbackLine = 0,
        int fallbackCharacter = 0,
        ProjectSourceIndex? sourceIndex = null)
    {
        expression = string.Empty;
        contextVariableName = null;
        extractionOrigin = null;

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

        var candidates = ResolveHelperMethodDeclarations(invocation, semanticModel, fallbackFilePath)
            .Where(m => string.Equals(m.Identifier.Text, helperName, StringComparison.Ordinal)
                && m.ParameterList.Parameters.Count == arguments.Count)
            .OrderByDescending(m => ComputeHelperMethodCandidateScore(m, arguments))
            .ToArray();

        foreach (var method in candidates)
        {
            var expressionParameterIndexes = GetExpressionParameterIndexes(method.ParameterList.Parameters);
            if (expressionParameterIndexes.Count == 0)
            {
                continue;
            }

            // Allow synthesis when the cursor is anywhere within the invocation -
            // including on the receiver, method name, or a non-expression argument.
            // The downstream guards (TryFindPrimaryQueryInvocation, IsLikelyQueryChain)
            // already reject methods whose bodies contain no EF query chain, so no
            // additional position narrowing is needed here.
            if (!IsCursorRelevantToInvocation(cursorPosition, invocation))
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

            var parsed = BindHelperMethodParameters(
                queryExpression,
                method.ParameterList.Parameters,
                arguments,
                invocation);

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

            expression = PreNormalizeExtractedExpression(parsed.ToString());
            contextVariableName = rootContext;
            var origin = BuildExtractionOriginSnapshot(
                queryInvocation,
                "helper-method",
                fallbackFilePath,
                fallbackLine,
                fallbackCharacter);
            if (!IsSourceBackedOrigin(origin, fallbackFilePath, fallbackLine, fallbackCharacter))
            {
                continue;
            }

            extractionOrigin = origin;
            return true;
        }

        return false;
    }

    private static bool TryExtractFromQueryableReturningHelperCall(
        SyntaxNode root,
        InvocationExpressionSyntax invocation,
        int cursorPosition,
        SemanticModel? semanticModel,
        out string expression,
        out string? contextVariableName,
        out ExtractionOriginSnapshot? extractionOrigin,
        string? fallbackFilePath = null,
        int fallbackLine = 0,
        int fallbackCharacter = 0,
        ProjectSourceIndex? sourceIndex = null)
    {
        expression = string.Empty;
        contextVariableName = null;
        extractionOrigin = null;

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

        var candidates = ResolveHelperMethodDeclarations(invocation, semanticModel, fallbackFilePath)
            .Where(m => string.Equals(m.Identifier.Text, helperName, StringComparison.Ordinal)
                && m.ParameterList.Parameters.Count == arguments.Count)
            .OrderByDescending(m => ComputeHelperMethodCandidateScore(m, arguments))
            .ToArray();

        foreach (var method in candidates)
        {
            if (!IsCursorRelevantToInvocation(cursorPosition, invocation))
            {
                continue;
            }

            if (!LooksLikeQueryableReturnType(method.ReturnType))
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

            var parsed = BindHelperMethodParameters(
                queryExpression,
                method.ParameterList.Parameters,
                arguments,
                invocation);

            if (parsed is InvocationExpressionSyntax parsedInvocation
                && !IsLikelyQueryChain(GetOutermostInvocationChain(parsedInvocation)))
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

            expression = PreNormalizeExtractedExpression(parsed.ToString());
            contextVariableName = rootContext;
            var origin = BuildExtractionOriginSnapshot(
                queryInvocation,
                "helper-method",
                fallbackFilePath,
                fallbackLine,
                fallbackCharacter);
            if (!IsSourceBackedOrigin(origin, fallbackFilePath, fallbackLine, fallbackCharacter))
            {
                continue;
            }

            extractionOrigin = origin;
            return true;
        }

        return false;
    }

    private static bool LooksLikeQueryableReturnType(TypeSyntax? returnType)
    {
        var returnTypeText = returnType?.ToString();
        if (string.IsNullOrWhiteSpace(returnTypeText))
        {
            return false;
        }

        return returnTypeText.Contains("IQueryable<", StringComparison.Ordinal)
               || returnTypeText.Contains("IOrderedQueryable<", StringComparison.Ordinal);
    }

    private static int ComputeHelperMethodCandidateScore(
        MethodDeclarationSyntax method,
        SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        var score = 0;
        var parameters = method.ParameterList.Parameters;
        var count = Math.Min(parameters.Count, arguments.Count);
        for (var i = 0; i < count; i++)
        {
            var parameter = parameters[i];
            var argument = arguments[i].Expression;
            var parameterName = parameter.Identifier.ValueText;
            var parameterType = parameter.Type?.ToString() ?? string.Empty;

            if (argument is IdentifierNameSyntax id
                && string.Equals(id.Identifier.ValueText, parameterName, StringComparison.Ordinal))
            {
                score += 4;
            }

            if (argument is LambdaExpressionSyntax && parameterType.Contains("Expression<", StringComparison.Ordinal))
            {
                score += 3;
            }
            else if (argument is not LambdaExpressionSyntax && !parameterType.Contains("Expression<", StringComparison.Ordinal))
            {
                score += 1;
            }
        }

        return score;
    }

    private static IReadOnlyList<MethodDeclarationSyntax> ResolveHelperMethodDeclarations(
        InvocationExpressionSyntax invocation,
        SemanticModel? semanticModel,
        string? currentFilePath)
    {
        if (semanticModel is null)
            return [];

        var invocationForModel = invocation;
        if (!ReferenceEquals(semanticModel.SyntaxTree, invocation.SyntaxTree))
        {
            var modelRoot = semanticModel.SyntaxTree.GetRoot();
            var mappedNode = modelRoot.FindNode(invocation.Span, getInnermostNodeForTie: true);
            if (mappedNode is InvocationExpressionSyntax mappedInvocation)
            {
                invocationForModel = mappedInvocation;
            }
            else
            {
                invocationForModel = modelRoot
                    .DescendantNodes(invocation.Span)
                    .OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(i => i.Span.Equals(invocation.Span))
                    ?? invocationForModel;
            }
        }

        var symbolInfo = semanticModel.GetSymbolInfo(invocationForModel);
        var methodSymbol = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (methodSymbol is null)
        {
            return ResolveFromReceiverTypeFallback(invocationForModel, semanticModel, currentFilePath);
        }

        var declarations = new List<MethodDeclarationSyntax>();
        foreach (var syntaxReference in methodSymbol.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is MethodDeclarationSyntax declaration)
            {
                declarations.Add(declaration);
            }
        }

        if (declarations.Count == 0)
        {
            declarations.AddRange(ResolveMetadataMethodDeclarations(methodSymbol, currentFilePath));
        }

        return declarations;
    }

    private static IReadOnlyList<MethodDeclarationSyntax> ResolveFromReceiverTypeFallback(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        string? currentFilePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return [];

        var methodName = memberAccess.Name.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(methodName))
            return [];

        var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression).Type;
        var receiverTypeName = receiverType?.Name;
        if (string.IsNullOrWhiteSpace(receiverTypeName)
            && memberAccess.Expression is IdentifierNameSyntax receiverIdentifier)
        {
            receiverTypeName = TryResolvePartialPrimaryConstructorParameterTypeName(
                invocation,
                receiverIdentifier.Identifier.ValueText,
                currentFilePath);
        }
        if (string.IsNullOrWhiteSpace(receiverTypeName))
            return [];

        return ResolveMethodsByContainingTypeName(
            receiverTypeName!,
            methodName,
            invocation.ArgumentList.Arguments.Count,
            currentFilePath);
    }

    private static string? TryResolvePartialPrimaryConstructorParameterTypeName(
        InvocationExpressionSyntax invocation,
        string parameterName,
        string? currentFilePath)
    {
        if (string.IsNullOrWhiteSpace(currentFilePath))
            return null;

        var className = invocation
            .FirstAncestorOrSelf<ClassDeclarationSyntax>()?
            .Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(className))
            return null;

        var projectDir = AssemblyResolver.TryGetProjectDirectory(currentFilePath!);
        if (string.IsNullOrWhiteSpace(projectDir))
            return null;

        foreach (var file in Directory.EnumerateFiles(projectDir, $"{className}*.cs", SearchOption.AllDirectories))
        {
            var relative = file[(projectDir.Length + 1)..];
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(source, path: file).GetRoot();
            }
            catch
            {
                continue;
            }

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (!string.Equals(classDecl.Identifier.ValueText, className, StringComparison.Ordinal))
                    continue;

                var parameter = classDecl.ParameterList?.Parameters
                    .FirstOrDefault(p => string.Equals(p.Identifier.ValueText, parameterName, StringComparison.Ordinal));
                if (parameter?.Type is null)
                    continue;

                if (parameter.Type is IdentifierNameSyntax idType)
                    return idType.Identifier.ValueText;

                return parameter.Type.ToString();
            }
        }

        return null;
    }

    private static IReadOnlyList<MethodDeclarationSyntax> ResolveMetadataMethodDeclarations(
        IMethodSymbol methodSymbol,
        string? currentFilePath)
    {
        if (string.IsNullOrWhiteSpace(currentFilePath))
            return [];

        var projectDir = AssemblyResolver.TryGetProjectDirectory(currentFilePath!);
        if (string.IsNullOrWhiteSpace(projectDir))
            return [];

        var typeName = methodSymbol.ContainingType?.Name;
        if (string.IsNullOrWhiteSpace(typeName))
            return [];

        return ResolveMethodsByContainingTypeName(
            typeName!,
            methodSymbol.Name,
            methodSymbol.Parameters.Length,
            currentFilePath);
    }

    private static IReadOnlyList<MethodDeclarationSyntax> ResolveMethodsByContainingTypeName(
        string containingTypeName,
        string methodName,
        int parameterCount,
        string? currentFilePath)
    {
        if (string.IsNullOrWhiteSpace(currentFilePath))
            return [];

        var projectDir = AssemblyResolver.TryGetProjectDirectory(currentFilePath!);
        if (string.IsNullOrWhiteSpace(projectDir))
            return [];

        var searchDirs = new List<string> { projectDir };
        searchDirs.AddRange(AssemblyResolver.TryGetProjectReferenceDirs(projectDir));

        var candidates = new List<MethodDeclarationSyntax>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in searchDirs)
        {
            foreach (var path in Directory.EnumerateFiles(dir, $"{containingTypeName}*.cs", SearchOption.AllDirectories))
            {
                if (!seenFiles.Add(path))
                    continue;

                var relative = path[(dir.Length + 1)..];
                if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                string source;
                try
                {
                    source = File.ReadAllText(path);
                }
                catch
                {
                    continue;
                }

                SyntaxNode root;
                try
                {
                    root = CSharpSyntaxTree.ParseText(source, path: path).GetRoot();
                }
                catch
                {
                    continue;
                }

                foreach (var declaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (!string.Equals(declaration.Identifier.ValueText, methodName, StringComparison.Ordinal))
                        continue;
                    if (declaration.ParameterList.Parameters.Count != parameterCount)
                        continue;

                    candidates.Add(declaration);
                }
            }
        }

        return candidates;
    }

    private static bool IsCursorRelevantToInvocation(int cursorPosition, InvocationExpressionSyntax invocation)
    {
        if (invocation.Span.Contains(cursorPosition))
        {
            return true;
        }

        var declarator = invocation.FirstAncestorOrSelf<VariableDeclaratorSyntax>();
        if (declarator is not null
            && declarator.Identifier.Span.Contains(cursorPosition)
            && declarator.Initializer?.Value is not null
            && declarator.Initializer.Value.Span.Contains(invocation.Span))
        {
            return true;
        }

        var assignment = invocation.FirstAncestorOrSelf<AssignmentExpressionSyntax>();
        if (assignment is not null
            && assignment.Left.Span.Contains(cursorPosition)
            && assignment.Right.Span.Contains(invocation.Span))
        {
            return true;
        }

        return false;
    }

    private static ExtractionOriginSnapshot BuildExtractionOriginSnapshot(
        SyntaxNode node,
        string scope,
        string? fallbackFilePath = null,
        int fallbackLine = 0,
        int fallbackCharacter = 0)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        var start = lineSpan.StartLinePosition;
        var end = lineSpan.EndLinePosition;
        var hasPath = !string.IsNullOrWhiteSpace(lineSpan.Path);
        var hasUsefulSpan = start.Line >= 0 && start.Character >= 0 && end.Line >= 0 && end.Character >= 0;

        return new ExtractionOriginSnapshot
        {
            FilePath = hasPath ? lineSpan.Path : fallbackFilePath,
            Line = hasUsefulSpan ? start.Line : fallbackLine,
            Character = hasUsefulSpan ? start.Character : fallbackCharacter,
            EndLine = hasUsefulSpan ? end.Line : fallbackLine,
            EndCharacter = hasUsefulSpan ? end.Character : fallbackCharacter,
            Scope = scope,
        };
    }

    private static bool IsSourceBackedOrigin(
        ExtractionOriginSnapshot origin,
        string? fallbackFilePath,
        int fallbackLine,
        int fallbackCharacter)
    {
        if (origin.Line < 0 || origin.Character < 0)
        {
            return false;
        }

        // In some unit/in-memory paths we intentionally do not carry a file path.
        // In those cases, line/char provenance is sufficient for helper extraction.
        if (string.IsNullOrWhiteSpace(fallbackFilePath))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(origin.FilePath))
        {
            return false;
        }

        return !(origin.Line == fallbackLine && origin.Character == fallbackCharacter);
    }

    private static bool TryGetRhsExpressionFromDeclarationOrAssignment(
        SyntaxNode? node,
        out ExpressionSyntax expression)
    {
        expression = null!;

        var variableDeclarator = node?.FirstAncestorOrSelf<VariableDeclaratorSyntax>();
        if (variableDeclarator?.Initializer?.Value is ExpressionSyntax initializerExpression)
        {
            expression = initializerExpression;
            return true;
        }

        var assignment = node?.FirstAncestorOrSelf<AssignmentExpressionSyntax>();
        if (assignment is not null)
        {
            expression = assignment.Right;
            return true;
        }

        return false;
    }

    private static bool TryExtractConditionalBranchQueryAtPosition(
        SyntaxNode? node,
        int cursorPosition,
        out ExpressionSyntax? expression,
        out InvocationExpressionSyntax? invocation)
    {
        expression = null;
        invocation = null;

        var conditional = node?.FirstAncestorOrSelf<ConditionalExpressionSyntax>();
        if (conditional is null)
        {
            return false;
        }

        ExpressionSyntax branch = conditional.WhenTrue.Span.Contains(cursorPosition)
            ? conditional.WhenTrue
            : conditional.WhenFalse.Span.Contains(cursorPosition)
                ? conditional.WhenFalse
                : null!;

        if (branch is null)
        {
            return false;
        }

        branch = UnwrapParenthesized(branch);

        var invocationInBranch = node?.AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => branch.Span.Contains(i.Span))
            .Select(GetOutermostInvocationChain)
            .FirstOrDefault(i => branch.Span.Contains(i.Span) && IsLikelyQueryChain(i));
        if (invocationInBranch is not null)
        {
            invocation = invocationInBranch;
            expression = invocationInBranch;
            return true;
        }

        if (branch is InvocationExpressionSyntax branchInvocation)
        {
            var outermostBranchInvocation = GetOutermostInvocationChain(branchInvocation);
            if (IsLikelyQueryChain(outermostBranchInvocation))
            {
                invocation = outermostBranchInvocation;
                expression = outermostBranchInvocation;
                return true;
            }
        }

        if (branch is IdentifierNameSyntax identifier)
        {
            var anchorStatement = conditional.FirstAncestorOrSelf<StatementSyntax>();
            if (anchorStatement is not null
                && TryResolveLocalExpressionCore(
                    identifier.Identifier.ValueText,
                    anchorStatement,
                    out var resolvedExpression,
                    out _))
            {
                resolvedExpression = UnwrapParenthesized(resolvedExpression);
                if (resolvedExpression is InvocationExpressionSyntax resolvedInvocation)
                {
                    var outermostResolvedInvocation = GetOutermostInvocationChain(resolvedInvocation);
                    if (IsLikelyQueryChain(outermostResolvedInvocation))
                    {
                        invocation = outermostResolvedInvocation;
                        expression = outermostResolvedInvocation;
                        return true;
                    }
                }

                if (IsLikelyQueryableExpression(resolvedExpression))
                {
                    expression = resolvedExpression;
                    invocation = resolvedExpression as InvocationExpressionSyntax;
                    return true;
                }
            }
        }

        if (IsLikelyQueryableExpression(branch))
        {
            expression = branch;
            invocation = branch as InvocationExpressionSyntax;
            return true;
        }

        return false;
    }

    private static ExpressionSyntax UnwrapParenthesized(ExpressionSyntax expression)
    {
        var current = expression;
        while (current is ParenthesizedExpressionSyntax parenthesized)
        {
            current = parenthesized.Expression;
        }

        return current;
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

    private static ExpressionSyntax BindHelperMethodParameters(
        ExpressionSyntax queryExpression,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        InvocationExpressionSyntax invocationContext)
    {
        var parameterMap = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        for (var i = 0; i < parameters.Count && i < arguments.Count; i++)
        {
            var parameterName = parameters[i].Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(parameterName))
                continue;

            var argumentExpression = arguments[i].Expression;
            if (argumentExpression is IdentifierNameSyntax identifier
                && IsExpressionParameter(parameters[i])
                && TryResolveLocalExpression(identifier.Identifier.ValueText, invocationContext, out var resolvedExpression))
            {
                argumentExpression = resolvedExpression;
            }

            parameterMap[parameterName] = argumentExpression.WithoutTrivia();
        }

        if (parameterMap.Count == 0)
            return queryExpression;

        var rewritten = new HelperParameterBinder(parameterMap).Visit(queryExpression) as ExpressionSyntax;
        return rewritten ?? queryExpression;
    }

    private static bool IsExpressionParameter(ParameterSyntax parameter)
    {
        var typeText = parameter.Type?.ToString();
        return !string.IsNullOrWhiteSpace(typeText)
            && typeText.Contains("Expression<", StringComparison.Ordinal);
    }

    private static ExpressionSyntax TryInlineLocalIdentifierReferences(
        ExpressionSyntax expression,
        InvocationExpressionSyntax queryInvocation)
    {
        var anchorStatement = queryInvocation.FirstAncestorOrSelf<StatementSyntax>();
        if (anchorStatement is null)
            return expression;

        var rewritten = new LocalIdentifierInliner(anchorStatement).Visit(expression) as ExpressionSyntax;
        return rewritten ?? expression;
    }

    private sealed class LocalIdentifierInliner : CSharpSyntaxRewriter
    {
        private readonly StatementSyntax _anchorStatement;

        public LocalIdentifierInliner(StatementSyntax anchorStatement)
        {
            _anchorStatement = anchorStatement;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Parent is MemberAccessExpressionSyntax memberAccess
                && ReferenceEquals(memberAccess.Name, node))
            {
                return node;
            }

            if (!TryResolveLocalExpressionCore(
                    node.Identifier.ValueText,
                    _anchorStatement,
                    out var resolvedExpression,
                    out _))
            {
                return node;
            }

            if (resolvedExpression is IdentifierNameSyntax id
                && string.Equals(id.Identifier.ValueText, node.Identifier.ValueText, StringComparison.Ordinal))
            {
                return node;
            }

            return SyntaxFactory.ParenthesizedExpression(resolvedExpression.WithoutTrivia());
        }
    }

    private sealed class HelperParameterBinder : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyDictionary<string, ExpressionSyntax> _parameterMap;

        public HelperParameterBinder(IReadOnlyDictionary<string, ExpressionSyntax> parameterMap)
        {
            _parameterMap = parameterMap;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Parent is MemberAccessExpressionSyntax memberAccess
                && ReferenceEquals(memberAccess.Name, node))
            {
                return base.VisitIdentifierName(node);
            }

            if (_parameterMap.TryGetValue(node.Identifier.ValueText, out var replacement))
                return replacement.WithTriviaFrom(node);

            return base.VisitIdentifierName(node);
        }
    }

    private sealed class InaccessibleProjectionTypeRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _semanticModel;
        private readonly Compilation _compilation;

        public InaccessibleProjectionTypeRewriter(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel;
            _compilation = semanticModel.Compilation;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node);
            if (visited?.Expression is not MemberAccessExpressionSyntax memberAccess
                || !string.Equals(memberAccess.Name.Identifier.ValueText, "Select", StringComparison.Ordinal)
                || visited.ArgumentList.Arguments.Count != 1)
            {
                return visited;
            }

            var argument = visited.ArgumentList.Arguments[0];
            var rewrittenLambda = TryRewriteProjectionLambda(argument.Expression);
            if (rewrittenLambda is null)
                return visited;

            var rewrittenArgument = argument.WithExpression(rewrittenLambda);
            return visited.WithArgumentList(
                visited.ArgumentList.WithArguments(
                    SyntaxFactory.SingletonSeparatedList(rewrittenArgument)));
        }

        private ExpressionSyntax? TryRewriteProjectionLambda(ExpressionSyntax lambdaExpression)
        {
            return lambdaExpression switch
            {
                SimpleLambdaExpressionSyntax simple when simple.Body is ObjectCreationExpressionSyntax objectCreation
                    => TryRewriteObjectCreation(simple, objectCreation),
                ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.Body is ObjectCreationExpressionSyntax objectCreation
                    => TryRewriteObjectCreation(parenthesized, objectCreation),
                _ => null,
            };
        }

        private ExpressionSyntax? TryRewriteObjectCreation(
            LambdaExpressionSyntax lambda,
            ObjectCreationExpressionSyntax objectCreation)
        {
            var semanticModel = ResolveSemanticModel(objectCreation);
            var createdType = semanticModel?.GetTypeInfo(objectCreation).Type as INamedTypeSymbol;
            if (createdType is not null && IsPubliclyAccessibleType(createdType))
                return null;

            if (createdType is null && !IsLikelyInaccessibleDeclaredType(objectCreation))
                return null;

            var anonymousProjection = BuildAnonymousProjection(objectCreation, semanticModel);
            return lambda switch
            {
                SimpleLambdaExpressionSyntax simple => simple.WithBody(anonymousProjection),
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.WithBody(anonymousProjection),
                _ => null,
            };
        }

        private SemanticModel? ResolveSemanticModel(SyntaxNode node)
        {
            if (node.SyntaxTree == _semanticModel.SyntaxTree)
                return _semanticModel;

            return _compilation.SyntaxTrees.Contains(node.SyntaxTree)
                ? _compilation.GetSemanticModel(node.SyntaxTree)
                : null;
        }

        private static bool IsLikelyInaccessibleDeclaredType(ObjectCreationExpressionSyntax objectCreation)
        {
            var typeName = objectCreation.Type switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(typeName))
                return false;

            var containingType = objectCreation.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (containingType is null)
                return false;

            foreach (var nestedType in containingType.Members.OfType<BaseTypeDeclarationSyntax>())
            {
                if (!string.Equals(nestedType.Identifier.ValueText, typeName, StringComparison.Ordinal))
                    continue;

                if (nestedType.Modifiers.Any(SyntaxKind.PublicKeyword))
                    return false;

                return true;
            }

            return false;
        }

        private AnonymousObjectCreationExpressionSyntax BuildAnonymousProjection(
            ObjectCreationExpressionSyntax objectCreation,
            SemanticModel? semanticModel)
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
                    .Where(m => m is not null)
                    .Cast<AnonymousObjectMemberDeclaratorSyntax>()
                    .ToList();

                if (membersFromInitializer.Count > 0)
                {
                    return SyntaxFactory.AnonymousObjectCreationExpression(
                        SyntaxFactory.SeparatedList(membersFromInitializer));
                }
            }

            var ctor = semanticModel?.GetSymbolInfo(objectCreation).Symbol as IMethodSymbol;
            var args = objectCreation.ArgumentList?.Arguments ?? default;
            var members = new List<AnonymousObjectMemberDeclaratorSyntax>();

            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                var memberName = arg.NameColon?.Name.Identifier.ValueText;
                if (string.IsNullOrWhiteSpace(memberName) && ctor is not null && i < ctor.Parameters.Length)
                    memberName = ctor.Parameters[i].Name;
                if (string.IsNullOrWhiteSpace(memberName))
                    memberName = TryInferMemberName(arg.Expression) ?? $"__ql{i}";

                members.Add(
                    SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.NameEquals(memberName!),
                        arg.Expression.WithoutTrivia()));
            }

            if (members.Count == 0)
            {
                members.Add(
                    SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.NameEquals("__ql0"),
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
            }

            return SyntaxFactory.AnonymousObjectCreationExpression(
                SyntaxFactory.SeparatedList(members));
        }

        private static bool IsPubliclyAccessibleType(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility != Accessibility.Public)
                    return false;
            }

            return true;
        }

        private static string? TryInferMemberName(ExpressionSyntax expression)
            => expression switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                _ => null,
            };
    }
}
