// Factory-root receiver substitution for LINQ chains in query preview.
// Detects patterns like: await _contextFactory.CreateDbContextAsync(ct) rooted queries
// Substitutes factory receiver with the context variable to enable proper expression capture.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    /// <summary>
    /// Attempts to detect a factory-root pattern and substitute the receiver with the
    /// context variable name. For example:
    ///   await _contextFactory.CreateDbContextAsync(ct).DbSet&lt;User&gt;()...
    /// becomes:
    ///   __qlContextForFactoryRoot.DbSet&lt;User&gt;()...
    ///
    /// Returns (rewritten expression, substitutionApplied, inferredContextTypeName) tuple.
    /// If no factory pattern is detected, returns the original expression with substitutionApplied=false.
    /// If substitution is applied but the inferred type is ambiguous (multiple factory candidates),
    /// returns the original expression with substitutionApplied=false to avoid semantic drift.
    /// </summary>
    internal static (string RewrittenExpression, bool SubstitutionApplied, string? FactoryContextType)
        TrySubstituteFactoryRoot(
            string expression,
            string contextVariableName,
            IReadOnlyList<string>? factoryCandidateTypeNames,
            Action<string>? debugLog = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return (expression, false, null);

        try
        {
            var parsed = SyntaxFactory.ParseExpression(expression);

            // Attempt to detect factory-root pattern in the parsed expression.
            var (factoryReceiver, isAsync, tContextType) = TryExtractFactoryRootPattern(
                parsed,
                factoryCandidateTypeNames,
                debugLog);

            if (factoryReceiver is null)
            {
                debugLog?.Invoke("factory-root-substitution skipped reason=no-factory-pattern-detected");
                return (expression, false, null);
            }

            // Ambiguity gate: if multiple factory candidates exist, skip substitution to avoid
            // semantic drift due to type confusion.
            if (factoryCandidateTypeNames?.Count > 1 && string.IsNullOrWhiteSpace(tContextType))
            {
                debugLog?.Invoke(
                    $"factory-root-substitution skipped reason=ambiguous-factory-candidates count={factoryCandidateTypeNames.Count}");
                return (expression, false, null);
            }

            // Determine the replacement receiver name.
            // Use a synthetic variable name to avoid conflicts with user code.
            const string replacementReceiverName = "__qlFactoryContext";

            // Walk the parsed expression and replace the factory receiver with the context variable.
            var rewriter = new FactoryRootRewriter(factoryReceiver, replacementReceiverName);
            var rewritten = rewriter.Visit(parsed);

            if (ReferenceEquals(rewritten, parsed))
            {
                debugLog?.Invoke("factory-root-substitution skipped reason=rewriter-produced-no-change");
                return (expression, false, null);
            }

            var rewrittenText = rewritten.WithoutTrivia().NormalizeWhitespace().ToString();
            debugLog?.Invoke(
                $"factory-root-substitution applied receiverType={tContextType ?? "unknown"} isAsync={isAsync}");

            return (rewrittenText, true, tContextType);
        }
        catch (Exception ex)
        {
            debugLog?.Invoke($"factory-root-substitution error={ex.GetType().Name}:{ex.Message}");
            return (expression, false, null);
        }
    }

    /// <summary>
    /// Detects a factory-root pattern in a parsed expression.
    /// Patterns:
    ///   - Async: await _contextFactory.CreateDbContextAsync(...)
    ///   - Sync: _contextFactory.CreateDbContext(...)
    ///
    /// Returns a tuple (factoryReceiver, isAsync, inferredContextType).
    /// factoryReceiver is the entire factory invocation syntax to be replaced.
    /// If no pattern is detected, returns (null, *, null).
    /// </summary>
    private static (InvocationExpressionSyntax? FactoryReceiver, bool IsAsync, string? ContextType)
        TryExtractFactoryRootPattern(
            ExpressionSyntax expression,
            IReadOnlyList<string>? factoryCandidateTypeNames,
            Action<string>? debugLog)
    {
        // If expression is a member access like: (await X).DbSet<T>()
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            // Check if the receiver (left side) is an await expression over a factory call.
            if (memberAccess.Expression is AwaitExpressionSyntax awaitExpr)
            {
                if (awaitExpr.Expression is InvocationExpressionSyntax factoryCall
                    && TryIdentifyFactoryCallPattern(factoryCall, out var contextTypeName))
                {
                    debugLog?.Invoke(
                        $"factory-root-detected pattern=await-factory-invoke contextType={contextTypeName}");
                    return (factoryCall, true, contextTypeName);
                }
            }

            // Check if the receiver is directly a factory call (sync pattern).
            if (memberAccess.Expression is InvocationExpressionSyntax syncFactoryCall
                && TryIdentifyFactoryCallPattern(syncFactoryCall, out var syncContextType))
            {
                debugLog?.Invoke(
                    $"factory-root-detected pattern=sync-factory-invoke contextType={syncContextType}");
                return (syncFactoryCall, false, syncContextType);
            }
        }

        // If the entire expression is an await over a factory (no chained member access after await).
        if (expression is AwaitExpressionSyntax awaitOnly)
        {
            if (awaitOnly.Expression is InvocationExpressionSyntax factoryCallOnly
                && TryIdentifyFactoryCallPattern(factoryCallOnly, out var contextTypeOnly))
            {
                debugLog?.Invoke(
                    $"factory-root-detected pattern=await-factory-only contextType={contextTypeOnly}");
                return (factoryCallOnly, true, contextTypeOnly);
            }
        }

        debugLog?.Invoke("factory-root-pattern-not-matched");
        return (null, false, null);
    }

    /// <summary>
    /// Identifies if an invocation matches a known factory call pattern:
    ///   - _factory.CreateDbContextAsync(ct)   (async)
    ///   - factory.CreateDbContext(args)       (sync)
    ///   - service.GetContext(ct)              (custom factory method, if inferred from context)
    ///
    /// Returns true if recognized, with the inferred context type name if available.
    /// </summary>
    private static bool TryIdentifyFactoryCallPattern(
        InvocationExpressionSyntax invocation,
        out string? contextTypeName)
    {
        contextTypeName = null;

        // Extract the method name being called.
        string? methodName = null;
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            methodName = memberAccess.Name.Identifier.ValueText;
        }
        else if (invocation.Expression is IdentifierNameSyntax identifier)
        {
            methodName = identifier.Identifier.ValueText;
        }

        if (string.IsNullOrWhiteSpace(methodName))
            return false;

        // Recognize standard EF Core factory method names.
        var isFactoryMethod = methodName switch
        {
            "CreateDbContextAsync" => true,
            "CreateDbContext" => true,
            "GetContext" => true,           // Custom factory method pattern
            "GetContextAsync" => true,      // Custom async factory pattern
            _ => false,
        };

        return isFactoryMethod;
    }

    /// <summary>
    /// Rewrites a parsed expression by replacing a specific factory invocation receiver
    /// with a substitution receiver name. This rewriter walks the syntax tree and replaces
    /// the exact factory receiver node with the new receiver.
    /// </summary>
    private sealed class FactoryRootRewriter : CSharpSyntaxRewriter
    {
        private readonly InvocationExpressionSyntax _originalReceiver;
        private readonly string _replacementReceiverName;
        private bool _substituted;

        public FactoryRootRewriter(InvocationExpressionSyntax originalReceiver, string replacementReceiverName)
        {
            _originalReceiver = originalReceiver;
            _replacementReceiverName = replacementReceiverName;
            _substituted = false;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            // If this invocation is the one we're looking for, don't visit further down.
            // Instead, replace it with an identifier to the new receiver name.
            if (ReferenceEquals(node.Expression, _originalReceiver.Expression)
                && !_substituted)
            {
                _substituted = true;
                // Return the expression unchanged but with the receiver replaced in parent.
                // This is handled in VisitMemberAccessExpression.
            }

            return base.VisitInvocationExpression(node);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            // Check if this member access has our target factory receiver as its Expression.
            if (node.Expression is AwaitExpressionSyntax awaitExpr)
            {
                if (awaitExpr.Expression is InvocationExpressionSyntax factoryCall
                    && SyntaxFactory.AreEquivalent(factoryCall, _originalReceiver)
                    && !_substituted)
                {
                    _substituted = true;
                    // Replace the await-factory with just the context variable name.
                    var newReceiver = SyntaxFactory.IdentifierName(_replacementReceiverName);
                    return node.WithExpression(newReceiver);
                }
            }

            if (node.Expression is InvocationExpressionSyntax syncFactoryCall
                && SyntaxFactory.AreEquivalent(syncFactoryCall, _originalReceiver)
                && !_substituted)
            {
                _substituted = true;
                // Replace the factory invocation with just the context variable name.
                var newReceiver = SyntaxFactory.IdentifierName(_replacementReceiverName);
                return node.WithExpression(newReceiver);
            }

            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitAwaitExpression(AwaitExpressionSyntax node)
        {
            // If the await's expression is the factory call we're replacing,
            // strip the await and replace with the context variable.
            if (node.Expression is InvocationExpressionSyntax factoryCall
                && SyntaxFactory.AreEquivalent(factoryCall, _originalReceiver)
                && !_substituted)
            {
                _substituted = true;
                return SyntaxFactory.IdentifierName(_replacementReceiverName);
            }

            return base.VisitAwaitExpression(node);
        }
    }
}
