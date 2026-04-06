// Implements slice-2 v2 capture planning: deterministic replay/placeholder/reject
// classification and diagnostics, with a compatibility adapter for runtime payloads.
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    public static bool TryBuildV2CapturePlan(
        string expression,
        string contextVariableName,
        string primarySourceText,
        int primaryLine,
        int primaryCharacter,
        string? targetAssemblyPath,
        out V2CapturePlanSnapshot? capturePlan,
        string? secondarySourceText = null,
        int? secondaryLine = null,
        int? secondaryCharacter = null,
        string? dbContextTypeName = null,
        Action<string>? debugLog = null,
        IReadOnlyList<string>? factoryCandidateTypeNames = null)
    {
        capturePlan = null;

        // Step 1: Attempt factory-root substitution before normal free-variable extraction.
        // This normalizes expressions like: await _contextFactory.CreateDbContextAsync(ct).DbSet<User>()
        // into: __qlFactoryContext.DbSet<User>() so that the synthetic receiver can be properly captured.
        var (normalizedExpression, factoryRootApplied, inferredFactoryContextType) = TrySubstituteFactoryRoot(
            expression,
            contextVariableName,
            factoryCandidateTypeNames,
            debugLog);

        // Use the normalized expression for the rest of the pipeline.
        var expressionToAnalyze = normalizedExpression;

        var graph = ExtractFreeVariableSymbolGraph(
            expressionToAnalyze,
            contextVariableName,
            primarySourceText,
            primaryLine,
            primaryCharacter,
            targetAssemblyPath,
            out var rewrittenExpression,
            secondarySourceText,
            secondaryLine,
            secondaryCharacter,
            dbContextTypeName,
            debugLog);

        var inferredLambdaMemberTypes = BuildLambdaParameterMemberTypeMap(
            SyntaxFactory.ParseExpression(rewrittenExpression),
            contextVariableName,
            targetAssemblyPath,
            dbContextTypeName,
            debugLog);

        // If factory root substitution was applied, we don't need to inject the receiver into the
        // capture plan. The synthetic receiver variable (__qlFactoryContext) will be provided
        // by the evaluation harness (QueryLens provides the actual DbContext instance). The receiver
        // is now just a regular identifier in the normalized expression, and since it's skipped
        // in CollectFreeVariableNames, it won't appear as an unresolved variable.
        capturePlan = BuildV2CapturePlanFromGraph(
            rewrittenExpression, 
            graph, 
            inferredLambdaMemberTypes);
        return capturePlan.IsComplete;
    }

    internal static V2CapturePlanSnapshot BuildV2CapturePlanFromGraph(
        string executableExpression,
        IReadOnlyList<LocalSymbolGraphEntry> graph)
    {
        return BuildV2CapturePlanFromGraph(executableExpression, graph, null);
    }

    internal static V2CapturePlanSnapshot BuildV2CapturePlanFromGraph(
        string executableExpression,
        IReadOnlyList<LocalSymbolGraphEntry> graph,
        IReadOnlyDictionary<(string Receiver, string Member), string>? inferredLambdaMemberTypes)
    {
        var ordered = graph
            .OrderBy(x => x.DeclarationOrder)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();

        var byName = ordered.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var diagnostics = new List<V2CaptureDiagnostic>();
        var entries = new List<V2CapturePlanEntry>(ordered.Length);

        foreach (var entry in ordered)
        {
            var planEntry = ClassifyCaptureEntry(
                entry,
                byName,
                executableExpression,
                inferredLambdaMemberTypes,
                diagnostics);
            entries.Add(planEntry);
        }

        // Synthetic factory receiver is skipped during free variable collection (see CollectFreeVariableNames).
        // It will be provided by the evaluation harness when the query is executed.

        // If executable expression accesses '<symbol>.Value', the symbol should be synthesized as
        // nullable to keep generated replay code compilable (e.g., Guid? x; x.Value).
        var valueReceivers = GetValueMemberAccessReceivers(executableExpression);
        if (valueReceivers.Count > 0)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (string.Equals(e.CapturePolicy, LocalSymbolReplayPolicies.Reject, StringComparison.Ordinal))
                    continue;

                if (!valueReceivers.Contains(e.Name))
                    continue;

                if (TryPromoteToNullableType(e.TypeName, out var nullableTypeName))
                {
                    entries[i] = e with { TypeName = nullableTypeName };
                }
            }
        }

        // Apply operator-context hints to UsePlaceholder entries so EvalSourceBuilder
        // can pick semantically accurate defaults (e.g., CancellationToken.None, typed lambdas).
        var capturedNames = entries
            .Where(e => string.Equals(e.CapturePolicy, LocalSymbolReplayPolicies.UsePlaceholder, StringComparison.Ordinal))
            .Select(e => e.Name);

        var usageHints = DetectQueryUsageHints(executableExpression, capturedNames);

        if (usageHints.Count > 0)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (string.Equals(e.CapturePolicy, LocalSymbolReplayPolicies.UsePlaceholder, StringComparison.Ordinal)
                    && usageHints.TryGetValue(e.Name, out var hint))
                {
                    entries[i] = e with { QueryUsageHint = hint };
                }
            }
        }

        return new V2CapturePlanSnapshot
        {
            ExecutableExpression = executableExpression,
            Entries = entries,
            Diagnostics = diagnostics,
            IsComplete = diagnostics.Count == 0,
        };
    }

    internal static IReadOnlyList<LocalSymbolGraphEntry> AdaptCapturePlanToLocalSymbolGraph(
        V2CapturePlanSnapshot capturePlan)
    {
        var adapted = new List<LocalSymbolGraphEntry>(capturePlan.Entries.Count);
        foreach (var entry in capturePlan.Entries
                     .Where(e => !string.Equals(e.CapturePolicy, LocalSymbolReplayPolicies.Reject, StringComparison.Ordinal))
                     .OrderBy(e => e.DeclarationOrder)
                     .ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            var replayPolicy = string.Equals(entry.CapturePolicy, LocalSymbolReplayPolicies.ReplayInitializer, StringComparison.Ordinal)
                ? LocalSymbolReplayPolicies.ReplayInitializer
                : LocalSymbolReplayPolicies.UsePlaceholder;

            adapted.Add(new LocalSymbolGraphEntry
            {
                Name = entry.Name,
                TypeName = entry.TypeName,
                Kind = entry.Kind,
                InitializerExpression = replayPolicy == LocalSymbolReplayPolicies.ReplayInitializer
                    ? entry.InitializerExpression
                    : null,
                DeclarationOrder = entry.DeclarationOrder,
                Dependencies = replayPolicy == LocalSymbolReplayPolicies.ReplayInitializer
                    ? entry.Dependencies
                    : [],
                Scope = entry.Scope,
                ReplayPolicy = replayPolicy,
            });
        }

        return adapted;
    }

    private static V2CapturePlanEntry ClassifyCaptureEntry(
        LocalSymbolGraphEntry entry,
        IReadOnlyDictionary<string, LocalSymbolGraphEntry> byName,
        string executableExpression,
        IReadOnlyDictionary<(string Receiver, string Member), string>? inferredLambdaMemberTypes,
        ICollection<V2CaptureDiagnostic> diagnostics)
    {
        if (string.Equals(entry.ReplayPolicy, LocalSymbolReplayPolicies.ReplayInitializer, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(entry.InitializerExpression))
            {
                diagnostics.Add(new V2CaptureDiagnostic
                {
                    Code = "QLV2_CAPTURE_INVALID_REPLAY",
                    Category = "replay-initializer-missing",
                    SymbolName = entry.Name,
                    Reason = "missing-initializer-expression",
                    Message = $"Capture '{entry.Name}' was marked for replay but has no initializer expression.",
                });

                return BuildRejectedEntry(entry, "QLV2_CAPTURE_INVALID_REPLAY", "Replay initializer is missing.");
            }

            var missingDependency = entry.Dependencies
                .FirstOrDefault(dep => !byName.ContainsKey(dep));
            if (!string.IsNullOrWhiteSpace(missingDependency))
            {
                diagnostics.Add(new V2CaptureDiagnostic
                {
                    Code = "QLV2_CAPTURE_MISSING_DEPENDENCY",
                    Category = "missing-dependency",
                    SymbolName = entry.Name,
                    Reason = "dependency-not-in-scope",
                    Message = $"Capture '{entry.Name}' depends on '{missingDependency}', which is not available in scope.",
                });

                return BuildRejectedEntry(entry, "QLV2_CAPTURE_MISSING_DEPENDENCY", $"Missing dependency '{missingDependency}'.");
            }

            if (ContainsUnsafeReplayInitializerSyntax(entry.InitializerExpression!))
            {
                // If the type is catalog-resolvable, downgrade to UsePlaceholder rather than
                // rejecting outright. This handles common cases like:
                //   var page = Math.Max(request.Page, 1);  → int → synthesize 1
                //   var pageSize = Math.Clamp(request.PageSize, 1, 200); → int → synthesize 1
                // The initializer is unsafe to replay, but a known or dependency-inferred type
                // can still be synthesized deterministically.
                if (TryGetPlaceholderTypeForEntry(entry, byName, executableExpression, inferredLambdaMemberTypes, out var placeholderType))
                {
                    return new V2CapturePlanEntry
                    {
                        Name = entry.Name,
                        TypeName = placeholderType,
                        Kind = entry.Kind,
                        InitializerExpression = null,
                        DeclarationOrder = entry.DeclarationOrder,
                        Dependencies = [],
                        Scope = entry.Scope,
                        CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                    };
                }

                diagnostics.Add(new V2CaptureDiagnostic
                {
                    Code = "QLV2_CAPTURE_UNSAFE_INITIALIZER",
                    Category = "unsafe-replay-initializer",
                    SymbolName = entry.Name,
                    Reason = "non-deterministic-or-executable-syntax",
                    Message = $"Capture '{entry.Name}' uses initializer syntax that is not allowed for deterministic replay and has no known placeholder type.",
                });

                return BuildRejectedEntry(
                    entry,
                    "QLV2_CAPTURE_UNSAFE_INITIALIZER",
                    "Replay initializer contains non-deterministic or executable syntax.");
            }

            var unsafeDependency = entry.Dependencies
                .Select(dep => byName[dep])
                .FirstOrDefault(IsReplayUnsafeDependency);
            if (unsafeDependency is not null)
            {
                // Same downgrade: if the type is catalog-known, synthesize a placeholder rather
                // than propagating the unsafe-dependency rejection.
                if (TryGetPlaceholderTypeForEntry(entry, byName, executableExpression, inferredLambdaMemberTypes, out var placeholderType))
                {
                    return new V2CapturePlanEntry
                    {
                        Name = entry.Name,
                        TypeName = placeholderType,
                        Kind = entry.Kind,
                        InitializerExpression = null,
                        DeclarationOrder = entry.DeclarationOrder,
                        Dependencies = [],
                        Scope = entry.Scope,
                        CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                    };
                }

                diagnostics.Add(new V2CaptureDiagnostic
                {
                    Code = "QLV2_CAPTURE_UNSAFE_DEPENDENCY",
                    Category = "unsafe-dependency",
                    SymbolName = entry.Name,
                    Reason = "depends-on-unsafe-capture",
                    Message = $"Capture '{entry.Name}' depends on unsafe capture '{unsafeDependency.Name}'.",
                });

                return BuildRejectedEntry(
                    entry,
                    "QLV2_CAPTURE_UNSAFE_DEPENDENCY",
                    $"Unsafe dependency '{unsafeDependency.Name}' blocks deterministic replay.");
            }

            return new V2CapturePlanEntry
            {
                Name = entry.Name,
                TypeName = entry.TypeName,
                Kind = entry.Kind,
                InitializerExpression = entry.InitializerExpression,
                DeclarationOrder = entry.DeclarationOrder,
                Dependencies = entry.Dependencies,
                Scope = entry.Scope,
                CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            };
        }

        if (string.IsNullOrWhiteSpace(entry.TypeName) || string.Equals(entry.TypeName, "?", StringComparison.Ordinal))
        {
            // Try dependency-based inference before rejecting unknown type. This covers
            // common inferred locals like:
            //   var safeLookbackDays = Math.Clamp(lookbackDays, 1, 365); // lookbackDays:int
            //   var fromUtc = utcNow.Date.AddDays(-safeLookbackDays);    // utcNow:DateTime
            if (TryInferPlaceholderTypeFromDependencies(entry, byName, out var inferredType)
                || TryInferPlaceholderTypeFromQueryUsage(entry.Name, executableExpression, inferredLambdaMemberTypes, out inferredType))
            {
                return new V2CapturePlanEntry
                {
                    Name = entry.Name,
                    TypeName = inferredType,
                    Kind = entry.Kind,
                    InitializerExpression = null,
                    DeclarationOrder = entry.DeclarationOrder,
                    Dependencies = [],
                    Scope = entry.Scope,
                    CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                };
            }

            diagnostics.Add(new V2CaptureDiagnostic
            {
                Code = "QLV2_CAPTURE_UNKNOWN_TYPE",
                Category = "unknown-type",
                SymbolName = entry.Name,
                Reason = "no-deterministic-type",
                Message = $"Capture '{entry.Name}' has no deterministic type and cannot use placeholder policy.",
            });

            return BuildRejectedEntry(entry, "QLV2_CAPTURE_UNKNOWN_TYPE", "Placeholder capture requires a concrete type.");
        }

        return new V2CapturePlanEntry
        {
            Name = entry.Name,
            TypeName = entry.TypeName,
            Kind = entry.Kind,
            InitializerExpression = null,
            DeclarationOrder = entry.DeclarationOrder,
            Dependencies = [],
            Scope = entry.Scope,
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };
    }

    private static V2CapturePlanEntry BuildRejectedEntry(
        LocalSymbolGraphEntry entry,
        string rejectCode,
        string rejectReason)
    {
        return new V2CapturePlanEntry
        {
            Name = entry.Name,
            TypeName = entry.TypeName,
            Kind = entry.Kind,
            InitializerExpression = entry.InitializerExpression,
            DeclarationOrder = entry.DeclarationOrder,
            Dependencies = entry.Dependencies,
            Scope = entry.Scope,
            CapturePolicy = LocalSymbolReplayPolicies.Reject,
            RejectCode = rejectCode,
            RejectReason = rejectReason,
        };
    }

    private static bool ContainsUnsafeReplayInitializerSyntax(string initializerExpression)
    {
        try
        {
            var expression = SyntaxFactory.ParseExpression(initializerExpression);
            return expression.DescendantNodesAndSelf().Any(n =>
                n is InvocationExpressionSyntax
                    or ObjectCreationExpressionSyntax
                    or AnonymousObjectCreationExpressionSyntax
                    or AssignmentExpressionSyntax
                    or AwaitExpressionSyntax
                    or QueryExpressionSyntax);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Returns true if the type name maps to a known entry in the placeholder synthesis catalog.
    /// Used to downgrade ReplayInitializer entries with unsafe initializers/dependencies to
    /// UsePlaceholder when the type can be deterministically synthesized.
    /// </summary>
    private static bool IsKnownPlaceholderType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var normalized = typeName.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();

        // Strip nullable suffix for catalog lookup
        var core = normalized.EndsWith("?", StringComparison.Ordinal) ? normalized[..^1] : normalized;

        // Collection placeholders are supported by EvalSourceBuilder and should be allowed as
        // safe downgrade targets for unsafe replay initializers.
        if (IsSupportedCollectionPlaceholderType(core))
            return true;

        return core is
            // Strings
            "string" or "String" or "System.String" or
            // Booleans
            "bool" or "Boolean" or "System.Boolean" or
            // Integer primitives
            "byte" or "Byte" or "System.Byte" or
            "sbyte" or "SByte" or "System.SByte" or
            "short" or "Int16" or "System.Int16" or
            "ushort" or "UInt16" or "System.UInt16" or
            "int" or "Int32" or "System.Int32" or
            "uint" or "UInt32" or "System.UInt32" or
            "long" or "Int64" or "System.Int64" or
            "ulong" or "UInt64" or "System.UInt64" or
            "nint" or "IntPtr" or "System.IntPtr" or
            "nuint" or "UIntPtr" or "System.UIntPtr" or
            // Floating-point
            "float" or "Single" or "System.Single" or
            "double" or "Double" or "System.Double" or
            "decimal" or "Decimal" or "System.Decimal" or
            // Char
            "char" or "Char" or "System.Char" or
            // Well-known structs
            "Guid" or "System.Guid" or
            "DateTime" or "System.DateTime" or
            "DateOnly" or "System.DateOnly" or
            "TimeOnly" or "System.TimeOnly" or
            "TimeSpan" or "System.TimeSpan" or
            "CancellationToken" or "System.Threading.CancellationToken";
    }

    private static bool IsSupportedCollectionPlaceholderType(string normalizedTypeName)
    {
        if (string.IsNullOrWhiteSpace(normalizedTypeName))
            return false;

        if (normalizedTypeName.EndsWith("[]", StringComparison.Ordinal))
            return true;

        return IsGenericCollectionType(normalizedTypeName, "IEnumerable")
            || IsGenericCollectionType(normalizedTypeName, "System.Collections.Generic.IEnumerable")
            || IsGenericCollectionType(normalizedTypeName, "ICollection")
            || IsGenericCollectionType(normalizedTypeName, "System.Collections.Generic.ICollection")
            || IsGenericCollectionType(normalizedTypeName, "List")
            || IsGenericCollectionType(normalizedTypeName, "System.Collections.Generic.List")
            || IsGenericCollectionType(normalizedTypeName, "IReadOnlyCollection")
            || IsGenericCollectionType(normalizedTypeName, "System.Collections.Generic.IReadOnlyCollection")
            || IsGenericCollectionType(normalizedTypeName, "IReadOnlyList")
            || IsGenericCollectionType(normalizedTypeName, "System.Collections.Generic.IReadOnlyList")
            || IsGenericCollectionType(normalizedTypeName, "ISet")
            || IsGenericCollectionType(normalizedTypeName, "System.Collections.Generic.ISet")
            || IsGenericCollectionType(normalizedTypeName, "HashSet")
            || IsGenericCollectionType(normalizedTypeName, "System.Collections.Generic.HashSet");
    }

    private static bool IsGenericCollectionType(string normalizedTypeName, string genericTypeName)
    {
        if (string.IsNullOrWhiteSpace(normalizedTypeName)
            || string.IsNullOrWhiteSpace(genericTypeName))
        {
            return false;
        }

        return normalizedTypeName.StartsWith(genericTypeName + "<", StringComparison.Ordinal)
            && normalizedTypeName.EndsWith(">", StringComparison.Ordinal);
    }

    private static bool TryGetPlaceholderTypeForEntry(
        LocalSymbolGraphEntry entry,
        IReadOnlyDictionary<string, LocalSymbolGraphEntry> byName,
        string executableExpression,
        IReadOnlyDictionary<(string Receiver, string Member), string>? inferredLambdaMemberTypes,
        out string typeName)
    {
        typeName = entry.TypeName;
        if (IsKnownPlaceholderType(typeName))
            return true;

        if ((TryInferPlaceholderTypeFromDependencies(entry, byName, out var inferredType)
             || TryInferPlaceholderTypeFromQueryUsage(entry.Name, executableExpression, inferredLambdaMemberTypes, out inferredType))
            && IsKnownPlaceholderType(inferredType))
        {
            typeName = inferredType;
            return true;
        }

        // Query usage may infer collection placeholder types like List<Enum> for
        // receiver patterns such as clearSections.Contains(d.Page).
        if (TryInferPlaceholderTypeFromQueryUsage(entry.Name, executableExpression, inferredLambdaMemberTypes, out inferredType))
        {
            typeName = inferredType;
            return true;
        }

        typeName = string.Empty;
        return false;
    }

    private static bool TryInferPlaceholderTypeFromDependencies(
        LocalSymbolGraphEntry entry,
        IReadOnlyDictionary<string, LocalSymbolGraphEntry> byName,
        out string inferredType)
    {
        inferredType = string.Empty;

        if (entry.Dependencies is null || entry.Dependencies.Count == 0)
            return false;

        var depTypes = entry.Dependencies
            .Where(byName.ContainsKey)
            .Select(dep => NormalizeTypeName(byName[dep].TypeName))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, "?", StringComparison.Ordinal))
            .Select(t => t.EndsWith("?", StringComparison.Ordinal) ? t[..^1] : t)
            .ToArray();

        if (depTypes.Length == 0)
            return false;

        // Date/time precedence for expressions like utcNow.Date.AddDays(-x)
        if (depTypes.Any(static t => t is "DateTime" or "System.DateTime"))
        {
            inferredType = "DateTime";
            return true;
        }

        if (depTypes.Any(static t => t is "DateOnly" or "System.DateOnly"))
        {
            inferredType = "DateOnly";
            return true;
        }

        if (depTypes.Any(static t => t is "TimeOnly" or "System.TimeOnly"))
        {
            inferredType = "TimeOnly";
            return true;
        }

        if (depTypes.Any(static t => t is "CancellationToken" or "System.Threading.CancellationToken"))
        {
            inferredType = "CancellationToken";
            return true;
        }

        // If all dependencies are numeric, prefer int as deterministic catalog default.
        var numericCount = depTypes.Count(IsNumericTypeName);
        if (numericCount == depTypes.Length)
        {
            inferredType = "int";
            return true;
        }

        // Single known dependency type: propagate.
        if (depTypes.Length == 1)
        {
            inferredType = depTypes[0];
            return true;
        }

        // Homogeneous dependencies: propagate the common type.
        if (depTypes.All(t => string.Equals(t, depTypes[0], StringComparison.Ordinal)))
        {
            inferredType = depTypes[0];
            return true;
        }

        return false;
    }

    private static bool TryInferPlaceholderTypeFromQueryUsage(
        string symbolName,
        string executableExpression,
        IReadOnlyDictionary<(string Receiver, string Member), string>? inferredLambdaMemberTypes,
        out string inferredType)
    {
        inferredType = string.Empty;

        if (string.IsNullOrWhiteSpace(symbolName)
            || string.IsNullOrWhiteSpace(executableExpression)
            || inferredLambdaMemberTypes is null
            || inferredLambdaMemberTypes.Count == 0)
        {
            return false;
        }

        try
        {
            var root = SyntaxFactory.ParseExpression(executableExpression);
            foreach (var invocation in root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                    continue;

                if (!string.Equals(memberAccess.Name.Identifier.ValueText, "Contains", StringComparison.Ordinal))
                    continue;

                if (memberAccess.Expression is not IdentifierNameSyntax receiverIdentifier
                    || !string.Equals(receiverIdentifier.Identifier.ValueText, symbolName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (invocation.ArgumentList.Arguments.Count != 1)
                    continue;

                var argExpression = invocation.ArgumentList.Arguments[0].Expression;
                if (argExpression is ParenthesizedExpressionSyntax parenthesized)
                    argExpression = parenthesized.Expression;

                if (argExpression is MemberAccessExpressionSyntax argMemberAccess
                    && argMemberAccess.Expression is IdentifierNameSyntax argReceiverIdentifier)
                {
                    var key = (argReceiverIdentifier.Identifier.ValueText, argMemberAccess.Name.Identifier.ValueText);
                    if (inferredLambdaMemberTypes.TryGetValue(key, out var elementType)
                        && !string.IsNullOrWhiteSpace(elementType))
                    {
                        inferredType = $"global::System.Collections.Generic.List<{elementType}>";
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Best effort only.
        }

        return false;
    }

    private static string NormalizeTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return string.Empty;

        return typeName.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static bool IsNumericTypeName(string typeName)
    {
        return typeName is
            "byte" or "Byte" or "System.Byte" or
            "sbyte" or "SByte" or "System.SByte" or
            "short" or "Int16" or "System.Int16" or
            "ushort" or "UInt16" or "System.UInt16" or
            "int" or "Int32" or "System.Int32" or
            "uint" or "UInt32" or "System.UInt32" or
            "long" or "Int64" or "System.Int64" or
            "ulong" or "UInt64" or "System.UInt64" or
            "nint" or "IntPtr" or "System.IntPtr" or
            "nuint" or "UIntPtr" or "System.UIntPtr" or
            "float" or "Single" or "System.Single" or
            "double" or "Double" or "System.Double" or
            "decimal" or "Decimal" or "System.Decimal";
    }

    private static HashSet<string> GetValueMemberAccessReceivers(string executableExpression)
    {
        var receivers = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(executableExpression))
            return receivers;

        try
        {
            var root = SyntaxFactory.ParseExpression(executableExpression);
            foreach (var access in root.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
            {
                if (!string.Equals(access.Name.Identifier.ValueText, "Value", StringComparison.Ordinal))
                    continue;

                if (TryExtractIdentifierReceiver(access.Expression, out var receiver))
                {
                    receivers.Add(receiver);
                }
            }
        }
        catch
        {
            // Best-effort only; do not block capture plan on parse issues.
        }

        return receivers;
    }

    private static bool TryExtractIdentifierReceiver(ExpressionSyntax expression, out string receiver)
    {
        receiver = string.Empty;

        switch (expression)
        {
            case IdentifierNameSyntax id:
                receiver = id.Identifier.ValueText;
                return !string.IsNullOrWhiteSpace(receiver);

            case ParenthesizedExpressionSyntax p:
                return TryExtractIdentifierReceiver(p.Expression, out receiver);

            case PostfixUnaryExpressionSyntax { OperatorToken.RawKind: (int)SyntaxKind.ExclamationToken } postfix:
                return TryExtractIdentifierReceiver(postfix.Operand, out receiver);

            default:
                return false;
        }
    }

    private static bool TryPromoteToNullableType(string? typeName, out string nullableTypeName)
    {
        nullableTypeName = string.Empty;
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var trimmed = typeName.Trim();
        if (trimmed.EndsWith("?", StringComparison.Ordinal))
            return false;

        // Generic/array types are not nullable in this form.
        if (trimmed.Contains('<', StringComparison.Ordinal)
            || trimmed.Contains('[', StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = NormalizeTypeName(trimmed);
        if (normalized is "string" or "String" or "System.String"
            or "object" or "Object" or "System.Object"
            or "dynamic")
        {
            return false;
        }

        nullableTypeName = trimmed + "?";
        return true;
    }
}
