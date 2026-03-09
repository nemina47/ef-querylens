using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static class MethodQueryInliner
{
    public sealed record InlineSelectorDiagnostics
    {
        public bool SelectorParameterDetected { get; init; }
        public bool SelectorArgumentSubstituted { get; init; }
        public bool SelectorArgumentSanitized { get; init; }
        public bool ContainsNestedSelectorMaterialization { get; init; }
    }

    private sealed record ParameterMapBuildResult(
        Dictionary<string, ExpressionSyntax> Map,
        bool SelectorParameterDetected,
        bool SelectorArgumentSubstituted,
        bool SelectorArgumentSanitized,
        bool ContainsNestedSelectorMaterialization);

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

    private static readonly HashSet<string> NestedMaterializationMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ToList", "ToListAsync", "ToArray", "ToArrayAsync", "ToDictionary", "ToDictionaryAsync", "ToLookup", "ToLookupAsync"
    };

    public static bool TryInlineTopLevelInvocation(
        string sourceText,
        string sourceFilePath,
        string expression,
        bool substituteSelectorArguments,
        out string inlinedExpression,
        out string? contextVariableName,
        out string? reason)
    {
        return TryInlineTopLevelInvocation(
            sourceText,
            sourceFilePath,
            expression,
            substituteSelectorArguments,
            out inlinedExpression,
            out contextVariableName,
            out _,
            out _,
            out reason);
    }

    public static bool TryInlineTopLevelInvocation(
        string sourceText,
        string sourceFilePath,
        string expression,
        bool substituteSelectorArguments,
        out string inlinedExpression,
        out string? contextVariableName,
        out string? selectedMethodSourcePath,
        out string? reason)
    {
        return TryInlineTopLevelInvocation(
            sourceText,
            sourceFilePath,
            expression,
            substituteSelectorArguments,
            out inlinedExpression,
            out contextVariableName,
            out selectedMethodSourcePath,
            out _,
            out reason);
    }

    public static bool TryInlineTopLevelInvocation(
        string sourceText,
        string sourceFilePath,
        string expression,
        bool substituteSelectorArguments,
        out string inlinedExpression,
        out string? contextVariableName,
        out string? selectedMethodSourcePath,
        out InlineSelectorDiagnostics diagnostics,
        out string? reason)
    {
        inlinedExpression = expression;
        contextVariableName = null;
        selectedMethodSourcePath = null;
        diagnostics = new InlineSelectorDiagnostics();
        reason = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            reason = "Expression was empty.";
            return false;
        }

        if (!TryParseTopLevelInvocation(
                expression,
                out var parsedExpression,
                out var topInvocation,
                out var rootName,
                out var methodName))
        {
            reason = "Expression was not a top-level member invocation.";
            return false;
        }

        // Avoid work for already-query-shaped roots.
        if (string.Equals(rootName, "db", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rootName, "context", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rootName, "dbContext", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Invocation root already looks like a DbContext variable.";
            return false;
        }

        var searchRoot = FindSearchRoot(sourceFilePath);
        if (searchRoot == null)
        {
            reason = "Could not determine source search root.";
            return false;
        }

        var argumentExpressions = topInvocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .ToArray();

        var candidates = FindMethodCandidates(searchRoot, sourceFilePath, sourceText, methodName, argumentExpressions.Length)
            .ToList();

        if (candidates.Count == 0)
        {
            reason = "No method candidates were found for invocation.";
            return false;
        }

        var best = candidates
            .Select(c => new { Candidate = c, Score = ScoreCandidate(c.Method, argumentExpressions) })
            .OrderByDescending(x => x.Score)
            .First();

        if (best.Score < 0)
        {
            reason = "No method candidates matched invocation arguments.";
            return false;
        }

        var method = best.Candidate.Method;
        var calleeQuery = TryExtractReturnedQueryExpression(method);
        if (calleeQuery == null)
        {
            reason = "Candidate method did not expose a supported query return shape.";
            return false;
        }

        var mapResult = BuildParameterArgumentMap(method, argumentExpressions, substituteSelectorArguments);
        var substituted = (ExpressionSyntax)new ParameterSubstitutionRewriter(mapResult.Map).Visit(calleeQuery)!;
        var stripped = StripTrailingTerminalMethods(substituted);
        var rewrittenExpression = ReplaceInvocationInExpression(parsedExpression, topInvocation, stripped);
        var normalizedExpression = StripTrailingTerminalMethods(rewrittenExpression);

        diagnostics = new InlineSelectorDiagnostics
        {
            SelectorParameterDetected = mapResult.SelectorParameterDetected,
            SelectorArgumentSubstituted = mapResult.SelectorArgumentSubstituted,
            SelectorArgumentSanitized = mapResult.SelectorArgumentSanitized,
            ContainsNestedSelectorMaterialization = mapResult.ContainsNestedSelectorMaterialization,
        };

        var extractedRoot = TryExtractRootContextVariable(normalizedExpression);
        if (string.IsNullOrWhiteSpace(extractedRoot))
        {
            reason = "Inlined expression root could not be determined.";
            return false;
        }

        inlinedExpression = normalizedExpression.NormalizeWhitespace().ToString();
        contextVariableName = extractedRoot;
        selectedMethodSourcePath = best.Candidate.FilePath;
        return true;
    }

    private static ExpressionSyntax ReplaceInvocationInExpression(
        ExpressionSyntax parsedExpression,
        InvocationExpressionSyntax invocationToReplace,
        ExpressionSyntax replacement)
    {
        if (ReferenceEquals(parsedExpression, invocationToReplace))
        {
            return replacement;
        }

        var wrappedReplacement = SyntaxFactory.ParenthesizedExpression(replacement.WithoutTrivia());
        var rewritten = parsedExpression.ReplaceNode(invocationToReplace, wrappedReplacement);
        return rewritten.WithoutTrivia();
    }

    private static bool TryParseTopLevelInvocation(
        string expression,
        out ExpressionSyntax parsedExpression,
        out InvocationExpressionSyntax invocation,
        out string rootName,
        out string methodName)
    {
        parsedExpression = null!;
        invocation = null!;
        rootName = string.Empty;
        methodName = string.Empty;

        if (SyntaxFactory.ParseExpression(expression) is not ExpressionSyntax parsed)
        {
            return false;
        }

        var targetInvocation = parsed
            .DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax })
            .OrderBy(i => i.SpanStart)
            .FirstOrDefault();

        if (targetInvocation is null)
        {
            return false;
        }

        if (targetInvocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Expression is not IdentifierNameSyntax rootIdentifier)
        {
            return false;
        }

        parsedExpression = parsed;
        invocation = targetInvocation;
        rootName = rootIdentifier.Identifier.ValueText;
        methodName = memberAccess.Name.Identifier.ValueText;
        return true;
    }

    private static string? FindSearchRoot(string sourceFilePath)
    {
        var current = Path.GetDirectoryName(sourceFilePath);

        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.GetFiles(current, "*.sln").Length > 0)
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        current = Path.GetDirectoryName(sourceFilePath);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.GetFiles(current, "*.csproj").Length > 0)
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    private static IEnumerable<(string FilePath, MethodDeclarationSyntax Method)> FindMethodCandidates(
        string searchRoot,
        string sourceFilePath,
        string sourceText,
        string methodName,
        int argumentCount)
    {
        foreach (var filePath in EnumerateCSharpFiles(searchRoot, sourceFilePath))
        {
            string text;
            try
            {
                text = string.Equals(filePath, sourceFilePath, StringComparison.OrdinalIgnoreCase)
                    ? sourceText
                    : File.ReadAllText(filePath);
            }
            catch
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text);
            var root = tree.GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!string.Equals(method.Identifier.ValueText, methodName, StringComparison.Ordinal))
                {
                    continue;
                }

                var parameterCount = method.ParameterList.Parameters.Count;
                if (parameterCount < argumentCount)
                {
                    continue;
                }

                yield return (filePath, method);
            }
        }
    }

    private static IEnumerable<string> EnumerateCSharpFiles(string searchRoot, string preferredSourceFile)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(preferredSourceFile))
        {
            yielded.Add(preferredSourceFile);
            yield return preferredSourceFile;
        }

        var dirs = new Stack<string>();
        dirs.Push(searchRoot);

        while (dirs.Count > 0)
        {
            var current = dirs.Pop();

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var subDir in subDirs)
            {
                var name = Path.GetFileName(subDir);
                if (ShouldSkipDirectory(name))
                {
                    continue;
                }

                dirs.Push(subDir);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*.cs");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (yielded.Add(file))
                {
                    yield return file;
                }
            }
        }
    }

    private static bool ShouldSkipDirectory(string name)
    {
        return string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, ".vscode", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreCandidate(MethodDeclarationSyntax method, IReadOnlyList<ExpressionSyntax> arguments)
    {
        var parameters = method.ParameterList.Parameters;
        if (arguments.Count > parameters.Count)
        {
            return -1;
        }

        var score = 0;

        if (arguments.Count == parameters.Count)
        {
            score += 20;
        }
        else
        {
            var remaining = parameters.Skip(arguments.Count);
            if (remaining.Any(p => p.Default == null))
            {
                return -1;
            }

            score += 10;
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            var parameter = parameters[i];
            var parameterType = parameter.Type?.ToString() ?? string.Empty;
            var argument = arguments[i];

            if (parameterType.Contains("Expression", StringComparison.Ordinal) &&
                (argument is LambdaExpressionSyntax || argument is AnonymousMethodExpressionSyntax))
            {
                score += 5;
            }

            if (parameterType.Contains("CancellationToken", StringComparison.Ordinal))
            {
                score += argument is IdentifierNameSyntax id &&
                         (string.Equals(id.Identifier.ValueText, "ct", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(id.Identifier.ValueText, "cancellationToken", StringComparison.OrdinalIgnoreCase))
                    ? 4
                    : 1;
            }
        }

        return score;
    }

    private static ExpressionSyntax? TryExtractReturnedQueryExpression(MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody is { Expression: { } expressionBody })
        {
            return StripTrailingTerminalMethods(UnwrapAwait(expressionBody));
        }

        if (method.Body == null)
        {
            return null;
        }

        for (var i = 0; i < method.Body.Statements.Count; i++)
        {
            if (method.Body.Statements[i] is not ReturnStatementSyntax returnStatement)
            {
                continue;
            }

            if (returnStatement.Expression is not { } returnExpr)
            {
                continue;
            }

            if (TryExtractQueryLikeReturnExpression(returnExpr, out var queryLikeReturn))
            {
                return InlineMethodLocalQueryRoot(method, queryLikeReturn, i);
            }
        }

        if (TryExtractQueryFromWrapperReturn(method, out var extractedFromWrapper, out var wrapperStatementIndex)
            && extractedFromWrapper is not null)
        {
            return InlineMethodLocalQueryRoot(method, extractedFromWrapper, wrapperStatementIndex);
        }

        return null;
    }

    private static bool TryExtractQueryLikeReturnExpression(
        ExpressionSyntax returnExpression,
        out ExpressionSyntax queryExpression)
    {
        queryExpression = null!;

        var unwrapped = UnwrapAwait(returnExpression);
        switch (unwrapped)
        {
            case InvocationExpressionSyntax:
            case MemberAccessExpressionSyntax:
            case IdentifierNameSyntax:
            case ThisExpressionSyntax:
            case ParenthesizedExpressionSyntax:
            case CastExpressionSyntax:
                queryExpression = StripTrailingTerminalMethods(unwrapped);
                return true;

            default:
                return false;
        }
    }

    private static bool TryExtractQueryFromWrapperReturn(
        MethodDeclarationSyntax method,
        out ExpressionSyntax? queryExpression,
        out int statementIndex)
    {
        queryExpression = null;
        statementIndex = -1;

        var statements = method.Body!.Statements;
        if (statements.Count == 0)
        {
            return false;
        }

        for (var i = statements.Count - 1; i >= 0; i--)
        {
            if (statements[i] is not ReturnStatementSyntax returnStatement
                || returnStatement.Expression is null)
            {
                continue;
            }

            var referencedNames = returnStatement.Expression
                .DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Select(id => id.Identifier.ValueText)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.Ordinal);

            if (referencedNames.Count == 0)
            {
                continue;
            }

            var bestScore = int.MinValue;
            ExpressionSyntax? bestExpression = null;
            var bestStatementIndex = -1;

            for (var j = i - 1; j >= 0; j--)
            {
                if (!TryExtractAssignedQueryExpression(statements[j], out var variableName, out var candidateExpression))
                {
                    continue;
                }

                if (!referencedNames.Contains(variableName))
                {
                    continue;
                }

                var score = ScoreAssignedQueryCandidate(candidateExpression);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestExpression = candidateExpression;
                    bestStatementIndex = j;
                }
            }

            if (bestExpression is not null)
            {
                queryExpression = bestExpression;
                statementIndex = bestStatementIndex;
                return true;
            }
        }

        return false;
    }

    private static ExpressionSyntax InlineMethodLocalQueryRoot(
        MethodDeclarationSyntax method,
        ExpressionSyntax expression,
        int anchorStatementIndex)
    {
        if (method.Body is null)
        {
            return StripTrailingTerminalMethods(UnwrapAwait(expression));
        }

        var statements = method.Body.Statements;
        if (statements.Count == 0)
        {
            return StripTrailingTerminalMethods(UnwrapAwait(expression));
        }

        var parameterNames = method.ParameterList.Parameters
            .Select(p => p.Identifier.ValueText)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.Ordinal);

        var current = StripTrailingTerminalMethods(UnwrapAwait(expression));
        var currentAnchor = anchorStatementIndex;

        for (var depth = 0; depth < 16; depth++)
        {
            if (!TryGetLeftMostExpression(current, out var leftMost)
                || leftMost is not IdentifierNameSyntax identifier)
            {
                break;
            }

            var name = identifier.Identifier.ValueText;
            if (parameterNames.Contains(name))
            {
                break;
            }

            if (!TryResolveLocalDeclarationExpression(statements, currentAnchor, name, out var replacement, out var replacementIndex))
            {
                break;
            }

            current = current.ReplaceNode(leftMost, replacement.WithoutTrivia());
            currentAnchor = replacementIndex;
        }

        return current;
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

                case MemberAccessExpressionSyntax memberAccess:
                    current = memberAccess.Expression;
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

    private static bool TryResolveLocalDeclarationExpression(
        SyntaxList<StatementSyntax> statements,
        int anchorStatementIndex,
        string identifier,
        out ExpressionSyntax expression,
        out int statementIndex)
    {
        expression = null!;
        statementIndex = -1;

        var start = Math.Min(anchorStatementIndex - 1, statements.Count - 1);
        for (var i = start; i >= 0; i--)
        {
            if (statements[i] is not LocalDeclarationStatementSyntax declaration)
            {
                continue;
            }

            foreach (var variable in declaration.Declaration.Variables)
            {
                if (!string.Equals(variable.Identifier.ValueText, identifier, StringComparison.Ordinal)
                    || variable.Initializer?.Value is not { } initializer)
                {
                    continue;
                }

                expression = StripTrailingTerminalMethods(UnwrapAwait(initializer));
                statementIndex = i;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractAssignedQueryExpression(
        StatementSyntax statement,
        out string variableName,
        out ExpressionSyntax queryExpression)
    {
        variableName = string.Empty;
        queryExpression = null!;

        switch (statement)
        {
            case LocalDeclarationStatementSyntax localDeclaration:
            {
                foreach (var declarator in localDeclaration.Declaration.Variables)
                {
                    if (declarator.Initializer?.Value is not { } initializer)
                    {
                        continue;
                    }

                    if (!TryGetCandidateQueryExpression(initializer, out var candidate))
                    {
                        continue;
                    }

                    variableName = declarator.Identifier.ValueText;
                    queryExpression = candidate;
                    return true;
                }

                return false;
            }

            case ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax assignment
            }:
            {
                if (!TryGetAssignedIdentifierName(assignment.Left, out var assignedName))
                {
                    return false;
                }

                if (!TryGetCandidateQueryExpression(assignment.Right, out var assignedExpression))
                {
                    return false;
                }

                variableName = assignedName;
                queryExpression = assignedExpression;
                return true;
            }

            default:
                return false;
        }
    }

    private static bool TryGetCandidateQueryExpression(ExpressionSyntax expression, out ExpressionSyntax candidate)
    {
        candidate = null!;

        var unwrapped = UnwrapAwait(expression);
        if (unwrapped is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        var terminalName = memberAccess.Name.Identifier.ValueText;
        if (!TerminalMethods.Contains(terminalName))
        {
            return false;
        }

        candidate = invocation;
        return true;
    }

    private static bool TryGetAssignedIdentifierName(ExpressionSyntax left, out string identifierName)
    {
        identifierName = string.Empty;

        if (left is IdentifierNameSyntax identifier)
        {
            identifierName = identifier.Identifier.ValueText;
            return true;
        }

        return false;
    }

    private static int ScoreAssignedQueryCandidate(ExpressionSyntax expression)
    {
        var unwrapped = UnwrapAwait(expression);
        if (unwrapped is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return 0;
        }

        var terminalName = memberAccess.Name.Identifier.ValueText;
        var terminalScore = terminalName switch
        {
            "ToList" or "ToListAsync" or "ToArray" or "ToArrayAsync" or "ToDictionary" or "ToDictionaryAsync" => 100,
            "First" or "FirstOrDefault" or "FirstAsync" or "FirstOrDefaultAsync" => 60,
            "Single" or "SingleOrDefault" or "SingleAsync" or "SingleOrDefaultAsync" => 55,
            "Any" or "AnyAsync" => 40,
            "Count" or "CountAsync" or "LongCount" or "LongCountAsync" => 20,
            _ => 10,
        };

        var chainDepth = unwrapped.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Count();
        return terminalScore + chainDepth;
    }

    private static ExpressionSyntax UnwrapAwait(ExpressionSyntax expression)
    {
        if (expression is AwaitExpressionSyntax awaited)
        {
            return awaited.Expression;
        }

        return expression;
    }

    private static ParameterMapBuildResult BuildParameterArgumentMap(
        MethodDeclarationSyntax method,
        IReadOnlyList<ExpressionSyntax> arguments,
        bool substituteSelectorArguments)
    {
        var map = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        var parameters = method.ParameterList.Parameters;
        var selectorParameterDetected = false;
        var selectorArgumentSubstituted = false;
        var selectorArgumentSanitized = false;
        var containsNestedSelectorMaterialization = false;

        for (var i = 0; i < arguments.Count && i < parameters.Count; i++)
        {
            var name = parameters[i].Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var argument = arguments[i];
            var parameter = parameters[i];
            var isSelectorParameter = IsSelectorParameter(parameter, argument);

            if (isSelectorParameter)
            {
                selectorParameterDetected = true;
                if (ContainsNestedMaterialization(argument))
                {
                    containsNestedSelectorMaterialization = true;
                }
            }

            if (ShouldSkipSubstitution(parameter, argument, substituteSelectorArguments))
            {
                continue;
            }

            if (substituteSelectorArguments)
            {
                var originalArgument = argument;
                argument = SanitizeSelectorArgument(parameter, argument);
                if (!ReferenceEquals(originalArgument, argument))
                {
                    selectorArgumentSanitized = true;
                }
            }

            if (isSelectorParameter)
            {
                selectorArgumentSubstituted = true;
            }

            map[name] = argument;
        }

        return new ParameterMapBuildResult(
            map,
            selectorParameterDetected,
            selectorArgumentSubstituted,
            selectorArgumentSanitized,
            containsNestedSelectorMaterialization);
    }

    private static bool IsSelectorParameter(ParameterSyntax parameter, ExpressionSyntax argument)
    {
        var parameterType = parameter.Type?.ToString() ?? string.Empty;
        return parameterType.Contains("Expression", StringComparison.Ordinal)
               && (argument is LambdaExpressionSyntax || argument is AnonymousMethodExpressionSyntax);
    }

    private static bool ContainsNestedMaterialization(ExpressionSyntax argument)
    {
        if (argument is not LambdaExpressionSyntax && argument is not AnonymousMethodExpressionSyntax)
        {
            return false;
        }

        foreach (var invocation in argument.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess
                && NestedMaterializationMethods.Contains(memberAccess.Name.Identifier.ValueText))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkipSubstitution(
        ParameterSyntax parameter,
        ExpressionSyntax argument,
        bool substituteSelectorArguments)
    {
        var parameterType = parameter.Type?.ToString() ?? string.Empty;

        // In conservative mode keep selector parameters as identifiers so QueryEvaluator
        // can synthesize a safe typed placeholder when endpoint DTO types are unavailable.
        if (parameterType.Contains("Expression", StringComparison.Ordinal) &&
            (argument is LambdaExpressionSyntax || argument is AnonymousMethodExpressionSyntax))
        {
            return !substituteSelectorArguments;
        }

        // Keep member-access arguments (for example req.WorkflowType) as method
        // parameters to avoid introducing unresolved request DTO roots.
        if (argument is MemberAccessExpressionSyntax)
        {
            return true;
        }

        return false;
    }

    private static ExpressionSyntax SanitizeSelectorArgument(ParameterSyntax parameter, ExpressionSyntax argument)
    {
        var parameterType = parameter.Type?.ToString() ?? string.Empty;
        if (!parameterType.Contains("Expression", StringComparison.Ordinal))
        {
            return argument;
        }

        if (argument is not LambdaExpressionSyntax && argument is not AnonymousMethodExpressionSyntax)
        {
            return argument;
        }

        var rewritten = new ProjectionTypeSanitizer().Visit(argument) as ExpressionSyntax;
        return rewritten ?? argument;
    }

    private static ExpressionSyntax StripTrailingTerminalMethods(ExpressionSyntax expression)
    {
        var current = expression;

        while (current is InvocationExpressionSyntax invocation &&
               invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
               TerminalMethods.Contains(memberAccess.Name.Identifier.ValueText))
        {
            current = memberAccess.Expression;
        }

        return current;
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

    private sealed class ParameterSubstitutionRewriter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyDictionary<string, ExpressionSyntax> _map;
        private readonly Stack<HashSet<string>> _shadowedNames = new();

        public ParameterSubstitutionRewriter(IReadOnlyDictionary<string, ExpressionSyntax> map)
        {
            _map = map;
        }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
            _shadowedNames.Push(new HashSet<string>(StringComparer.Ordinal)
            {
                node.Parameter.Identifier.ValueText
            });

            var visited = base.VisitSimpleLambdaExpression(node);
            _shadowedNames.Pop();
            return visited;
        }

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            var scoped = new HashSet<string>(
                node.ParameterList.Parameters.Select(p => p.Identifier.ValueText),
                StringComparer.Ordinal);
            _shadowedNames.Push(scoped);

            var visited = base.VisitParenthesizedLambdaExpression(node);
            _shadowedNames.Pop();
            return visited;
        }

        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        {
            var scoped = new HashSet<string>(StringComparer.Ordinal);
            if (node.ParameterList != null)
            {
                foreach (var parameter in node.ParameterList.Parameters)
                {
                    scoped.Add(parameter.Identifier.ValueText);
                }
            }

            _shadowedNames.Push(scoped);
            var visited = base.VisitAnonymousMethodExpression(node);
            _shadowedNames.Pop();
            return visited;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var name = node.Identifier.ValueText;
            if (!_map.TryGetValue(name, out var replacement) || IsShadowed(name))
            {
                return base.VisitIdentifierName(node);
            }

            return replacement.WithTriviaFrom(node);
        }

        private bool IsShadowed(string name)
        {
            foreach (var scope in _shadowedNames)
            {
                if (scope.Contains(name))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class ProjectionTypeSanitizer : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;

            // Only sanitize object-initializer-style DTO projections; keep constructor-based
            // creations unchanged because they may rely on positional constructor semantics.
            if (visited.Initializer is null)
            {
                return visited;
            }

            if (visited.ArgumentList is { Arguments.Count: > 0 })
            {
                return visited;
            }

            var members = new List<string>();
            foreach (var initExpression in visited.Initializer.Expressions)
            {
                if (initExpression is AssignmentExpressionSyntax assignment)
                {
                    var memberName = assignment.Left switch
                    {
                        IdentifierNameSyntax id => id.Identifier.ValueText,
                        SimpleNameSyntax simple => simple.Identifier.ValueText,
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                        _ => null,
                    };

                    if (!string.IsNullOrWhiteSpace(memberName))
                    {
                        members.Add($"{memberName} = {assignment.Right}");
                        continue;
                    }
                }

                members.Add(initExpression.ToString());
            }

            var anonymousText = members.Count == 0
                ? "new { __ql = 1 }"
                : $"new {{ {string.Join(", ", members)} }}";

            return SyntaxFactory.ParseExpression(anonymousText)
                .WithTriviaFrom(node);
        }
    }
}