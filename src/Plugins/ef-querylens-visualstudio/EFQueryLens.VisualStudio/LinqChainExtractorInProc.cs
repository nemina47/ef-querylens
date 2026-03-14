// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// In-process LINQ extraction helper used by hover QuickInfo.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

internal static class LinqChainExtractorInProc
{
    private static readonly ImmutableHashSet<string> linqMethodNames = ImmutableHashSet.Create(
        "Where", "Select", "SelectMany", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "GroupBy", "Join", "GroupJoin", "Take", "Skip", "TakeWhile", "SkipWhile",
        "First", "FirstOrDefault", "Last", "LastOrDefault", "Single", "SingleOrDefault",
        "Any", "All", "Count", "LongCount", "Sum", "Min", "Max", "Average", "Aggregate",
        "ToList", "ToArray", "ToDictionary", "ToLookup", "AsEnumerable", "AsQueryable",
        "ToListAsync", "ToArrayAsync", "FirstAsync", "FirstOrDefaultAsync", "SingleAsync",
        "SingleOrDefaultAsync", "CountAsync", "AnyAsync", "AllAsync", "SumAsync", "MinAsync", "MaxAsync", "AverageAsync");

    public static (string? LinqCode, TextSpan? Span, string? ErrorMessage) TryExtractLinqAtPositionWithSpan(
        string documentText,
        int caretOffset)
    {
        if (string.IsNullOrEmpty(documentText))
            return (null, null, "Document is empty.");

        caretOffset = Math.Max(0, Math.Min(caretOffset, documentText.Length));
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(documentText);
        SyntaxNode root = syntaxTree.GetRoot();
        SyntaxNode node = root.FindNode(new TextSpan(caretOffset, 0), getInnermostNodeForTie: true);

        SyntaxNode? expression = FindLinqExpressionContaining(node);
        if (expression is null)
            return (null, null, "No LINQ expression found at cursor. Place the cursor on a LINQ query.");

        return (expression.GetText().ToString().Trim(), expression.Span, null);
    }

    private static SyntaxNode? FindLinqExpressionContaining(SyntaxNode node)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            if (current is InvocationExpressionSyntax invocation && IsLinqInvocation(invocation))
                return GetRootExpressionOfChain(invocation);
            if (current is MemberAccessExpressionSyntax memberAccess && IsLinqMemberAccess(memberAccess))
            {
                InvocationExpressionSyntax? topInvocation = GetTopInvocationInChain(memberAccess);
                if (topInvocation is not null)
                    return GetRootExpressionOfChain(topInvocation);
            }
            if (current is VariableDeclaratorSyntax { Initializer: not null } declarator)
            {
                SyntaxNode? inInitializer = FindFirstLinqExpressionIn(declarator.Initializer.Value);
                if (inInitializer is not null)
                    return inInitializer;
            }
            if (current is AssignmentExpressionSyntax assignment)
            {
                SyntaxNode? inRight = FindFirstLinqExpressionIn(assignment.Right);
                if (inRight is not null)
                    return inRight;
            }
        }

        return null;
    }

    private static SyntaxNode? FindFirstLinqExpressionIn(SyntaxNode expression)
    {
        foreach (SyntaxNode? n in expression.DescendantNodesAndSelf())
        {
            if (n is InvocationExpressionSyntax inv && IsLinqInvocation(inv))
                return GetRootExpressionOfChain(inv);
            if (n is MemberAccessExpressionSyntax m && IsLinqMemberAccess(m))
            {
                InvocationExpressionSyntax? top = GetTopInvocationInChain(m);
                if (top is not null)
                    return GetRootExpressionOfChain(top);
            }
        }

        return null;
    }

    private static bool IsLinqInvocation(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax x => x.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax x => x.Name.Identifier.ValueText,
            _ => null,
        };
        return name is not null && linqMethodNames.Contains(name);
    }

    private static bool IsLinqMemberAccess(MemberAccessExpressionSyntax memberAccess) =>
        linqMethodNames.Contains(memberAccess.Name.Identifier.ValueText);

    private static InvocationExpressionSyntax? GetTopInvocationInChain(MemberAccessExpressionSyntax memberAccess)
    {
        if (memberAccess.Parent is InvocationExpressionSyntax inv)
            return GetTopInvocation(inv);
        return null;
    }

    private static InvocationExpressionSyntax GetTopInvocation(InvocationExpressionSyntax invocation)
    {
        InvocationExpressionSyntax current = invocation;
        while (current.Parent is MemberAccessExpressionSyntax && current.Parent.Parent is InvocationExpressionSyntax parentInvocation)
            current = parentInvocation;
        return current;
    }

    private static SyntaxNode GetRootExpressionOfChain(InvocationExpressionSyntax invocation)
    {
        var current = (SyntaxNode)invocation;
        while (current.Parent is MemberAccessExpressionSyntax or InvocationExpressionSyntax)
            current = current.Parent!;
        return current;
    }
}

