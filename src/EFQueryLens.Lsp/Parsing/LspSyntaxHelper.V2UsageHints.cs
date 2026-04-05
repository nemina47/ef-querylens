// LspSyntaxHelper.V2UsageHints.cs — operator-context analysis for v2 capture plan entries.
// Walks the rewritten query expression to detect how each captured variable is used
// (Skip/Take, Select projection, CancellationToken, string predicates) and sets
// QueryUsageHint on V2CapturePlanEntry for context-aware placeholder synthesis.
using System;
using System.Collections.Generic;
using System.Linq;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    /// <summary>
    /// Walks the executable query expression and detects how each captured variable is used.
    /// Returns a dictionary from variable name to <see cref="QueryUsageHints"/> constant.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> DetectQueryUsageHints(
        string queryExpression,
        IEnumerable<string> capturedNames)
    {
        var hints = new Dictionary<string, string>(StringComparer.Ordinal);
        var nameSet = capturedNames.ToHashSet(StringComparer.Ordinal);

        if (nameSet.Count == 0 || string.IsNullOrWhiteSpace(queryExpression))
            return hints;

        try
        {
            var root = SyntaxFactory.ParseExpression(queryExpression);
            foreach (var invocation in root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                var methodName = ExtractSimpleMethodName(invocation);
                if (string.IsNullOrEmpty(methodName))
                    continue;

                var args = invocation.ArgumentList.Arguments;
                for (int i = 0; i < args.Count; i++)
                {
                    var arg = args[i];
                    var referencedNames = arg.DescendantNodesAndSelf()
                        .OfType<IdentifierNameSyntax>()
                        .Select(n => n.Identifier.ValueText)
                        .Where(nameSet.Contains)
                        .Distinct(StringComparer.Ordinal);

                    foreach (var name in referencedNames)
                    {
                        var hint = ClassifyUsageHint(methodName, i, args.Count, arg);
                        if (hint is not null)
                            hints.TryAdd(name, hint);
                    }
                }
            }
        }
        catch
        {
            // Parse failures are non-fatal; proceed without hints
        }

        return hints;
    }

    private static string? ClassifyUsageHint(
        string methodName,
        int argIndex,
        int totalArgs,
        ArgumentSyntax arg)
    {
        switch (methodName)
        {
            case "Skip":
            case "Take":
                return QueryUsageHints.SkipTake;

            case "Select":
            case "SelectMany":
                return argIndex == 0 ? QueryUsageHints.SelectorExpression : null;

            // Async terminal methods - last arg is typically CancellationToken
            case "ToListAsync":
            case "ToArrayAsync":
            case "FirstAsync":
            case "FirstOrDefaultAsync":
            case "SingleAsync":
            case "SingleOrDefaultAsync":
            case "CountAsync":
            case "LongCountAsync":
            case "AnyAsync":
            case "AllAsync":
            case "MaxAsync":
            case "MinAsync":
            case "SumAsync":
            case "AverageAsync":
            case "ExecuteDeleteAsync":
            case "ExecuteUpdateAsync":
                return argIndex == totalArgs - 1 ? QueryUsageHints.CancellationToken : null;

            case "Contains":
                // disambiguate string.Contains(value) vs collection.Contains(item)
                return argIndex == 0 ? QueryUsageHints.StringContains : null;

            case "StartsWith":
                return argIndex == 0 ? QueryUsageHints.StringPrefix : null;

            case "EndsWith":
                return argIndex == 0 ? QueryUsageHints.StringSuffix : null;

            default:
                return null;
        }
    }

    private static string ExtractSimpleMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => string.Empty,
        };
    }
}
