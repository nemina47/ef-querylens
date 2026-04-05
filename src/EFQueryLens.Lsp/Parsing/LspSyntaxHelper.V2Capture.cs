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
        Action<string>? debugLog = null)
    {
        capturePlan = null;

        var graph = ExtractFreeVariableSymbolGraph(
            expression,
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

        capturePlan = BuildV2CapturePlanFromGraph(rewrittenExpression, graph);
        return capturePlan.IsComplete;
    }

    internal static V2CapturePlanSnapshot BuildV2CapturePlanFromGraph(
        string executableExpression,
        IReadOnlyList<LocalSymbolGraphEntry> graph)
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
            var planEntry = ClassifyCaptureEntry(entry, byName, diagnostics);
            entries.Add(planEntry);
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
                // The initializer is unsafe to replay, but the type is fully known.
                if (IsKnownPlaceholderType(entry.TypeName))
                {
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
                if (IsKnownPlaceholderType(entry.TypeName))
                {
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
}
