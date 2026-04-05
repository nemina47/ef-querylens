using System;
using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Scripting.Compilation;

/// <summary>
/// V2 capture-plan support for EvalSourceBuilder.
/// Interprets v2 capture-plan policies (ReplayInitializer, UsePlaceholder, Reject)
/// to drive code generation for symbol initialization.
/// </summary>
internal static partial class EvalSourceBuilder
{
    /// <summary>
    /// Builds initialization code for a v2 capture-plan entry based on capture policy.
    /// </summary>
    /// <para>
    /// Policy interpretation:
    /// - ReplayInitializer: Emit normal replay initialization code (status quo)
    /// - UsePlaceholder: Emit default/placeholder value for the symbol type
    /// - Reject: Symbol should not be initialized; capture will fail
    /// </para>
    internal static string? BuildV2CaptureInitializationCode(V2CapturePlanEntry entry)
    {
        return entry.CapturePolicy switch
        {
            LocalSymbolReplayPolicies.ReplayInitializer 
                => BuildReplayInitializerCode(entry),
            
            LocalSymbolReplayPolicies.UsePlaceholder 
                => BuildPlaceholderInitializationCode(entry),
            
            LocalSymbolReplayPolicies.Reject 
                => null,
            
            _ => null,
        };
    }

    /// <summary>
    /// Builds standard replay initializer code for a symbol.
    /// This is the current generation path for symbols that can be captured.
    /// </summary>
    private static string BuildReplayInitializerCode(V2CapturePlanEntry entry)
    {
        // Use the entry's InitializerExpression if provided, otherwise fall back to default
        if (!string.IsNullOrWhiteSpace(entry.InitializerExpression))
        {
            return $"var {entry.Name} = {entry.InitializerExpression};";
        }

        // Fallback: default(T) for the symbol's type
        return $"var {entry.Name} = default({entry.TypeName});";
    }

    /// <summary>
    /// Builds placeholder initialization code for a symbol.
    /// Emits a deterministic placeholder value for the symbol type.
    /// If placeholder generation fails for unsupported types, emits a diagnostic and falls back to default(T).
    /// </summary>
    /// <summary>
    /// Builds placeholder initialization code for a symbol.
    /// Emits a deterministic placeholder value for the symbol type.
    /// If placeholder generation fails for unsupported types, emits a diagnostic and falls back to default(T).
    /// </summary>
    private static string BuildPlaceholderInitializationCode(V2CapturePlanEntry entry)
    {
        // Hint-driven path has highest priority — operator context can select a better default
        if (TryBuildHintDrivenPlaceholder(entry, out var hintPlaceholder))
        {
            return $"var {entry.Name} = {hintPlaceholder};";
        }

        // Expression placeholders should be synthesized even without hints (e.g. .Where(filter))
        // so query operators never receive null expression arguments.
        if (TryBuildSelectorExpressionPlaceholder(entry.TypeName, out var expressionPlaceholder))
        {
            return $"var {entry.Name} = {expressionPlaceholder};";
        }

        if (TryBuildCollectionPlaceholder(entry.TypeName, out var collectionPlaceholder))
        {
            return $"var {entry.Name} = {collectionPlaceholder};";
        }

        if (TryBuildScalarPlaceholder(entry.TypeName, out var scalarPlaceholder))
        {
            return $"var {entry.Name} = {scalarPlaceholder};";
        }

        // Unsupported type: emit diagnostic and fall back to default(T).
        var diagnostic = EvalSourceBuilderDiagnostics.DetailedPlaceholderUnsupported(
            entry.Name,
            entry.TypeName ?? "unknown");
        EvalSourceBuilderDiagnosticContextHolder.Current.Emit(diagnostic);

        return $"var {entry.Name} = default({entry.TypeName});";
    }

    /// <summary>
    /// Attempts to build a placeholder driven by the entry's <see cref="V2CapturePlanEntry.QueryUsageHint"/>.
    /// Provides operator-aware defaults that are more semantically accurate than type-only catalog defaults.
    /// </summary>
    private static bool TryBuildHintDrivenPlaceholder(V2CapturePlanEntry entry, out string placeholder)
    {
        placeholder = string.Empty;

        switch (entry.QueryUsageHint)
        {
            case QueryUsageHints.SelectorExpression:
                return TryBuildSelectorExpressionPlaceholder(entry.TypeName, out placeholder);

            case QueryUsageHints.StringPrefix:
                placeholder = "\"ql\"";
                return true;

            case QueryUsageHints.StringSuffix:
                placeholder = "\"stub\"";
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Attempts to generate a compilable LINQ expression for <c>Expression&lt;Func&lt;T, R&gt;&gt;</c> types.
    /// Scans for the Func type arguments and emits <c>e => (R)e</c> for value-compatible returns or
    /// <c>e => default(R)</c> as a fallback.
    /// </summary>
    private static bool TryBuildSelectorExpressionPlaceholder(string? typeName, out string placeholder)
    {
        placeholder = string.Empty;

        if (!TryExtractExpressionFuncTypeArgs(typeName, out var paramType, out var returnType))
            return false;

        // Normalize away global:: for the generated lambda body
        var normalizedReturn = NormalizeTypeName(returnType);

        if (string.Equals(normalizedReturn, "object", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedReturn, "System.Object", StringComparison.OrdinalIgnoreCase))
        {
            // Select * equivalent — project entity as object
            placeholder = $"({typeName})(e => (object)e)";
            return true;
        }

        if (string.Equals(normalizedReturn, "bool", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedReturn, "System.Boolean", StringComparison.OrdinalIgnoreCase))
        {
            // For predicate expressions, prefer a tautology to avoid WHERE FALSE artifacts.
            placeholder = $"({typeName})(e => true)";
            return true;
        }

        // For a scalar return type that matches the param type, use identity selector
        if (string.Equals(NormalizeTypeName(paramType), normalizedReturn, StringComparison.Ordinal))
        {
            placeholder = $"({typeName})(e => e)";
            return true;
        }

        // Generic typed return — emit default(R) to produce a valid expression tree
        placeholder = $"({typeName})(e => default({returnType}))";
        return true;
    }

    /// <summary>
    /// Extracts T and R from <c>Expression&lt;Func&lt;T, R&gt;&gt;</c> type name strings.
    /// Handles global:: prefixes and nested generic types.
    /// </summary>
    private static bool TryExtractExpressionFuncTypeArgs(
        string? typeName,
        out string paramType,
        out string returnType)
    {
        paramType = string.Empty;
        returnType = string.Empty;

        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        // Requires both "Expression" and "Func" in the type name
        if (!typeName.Contains("Expression", StringComparison.OrdinalIgnoreCase)
            || !typeName.Contains("Func", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Locate innermost "Func<" — handles "Func<" from both short and fully qualified names
        var funcIdx = typeName.LastIndexOf("Func<", StringComparison.OrdinalIgnoreCase);
        if (funcIdx < 0)
            return false;

        var argsStart = funcIdx + "Func<".Length;

        // Extract the content between angle brackets (depth-aware)
        if (!TryExtractAngleBracketContent(typeName, argsStart - 1, out var funcArgs))
            return false;

        // Split on "," at depth 0 to get T and R
        if (!TrySplitFirstDepthZeroComma(funcArgs, out var first, out var rest))
            return false;

        paramType = first.Trim();
        returnType = rest.Trim();
        return !string.IsNullOrEmpty(paramType) && !string.IsNullOrEmpty(returnType);
    }

    private static bool TryExtractAngleBracketContent(string text, int openAngleIdx, out string content)
    {
        content = string.Empty;
        if (openAngleIdx < 0 || openAngleIdx >= text.Length || text[openAngleIdx] != '<')
            return false;

        int depth = 0;
        int start = openAngleIdx + 1;
        for (int i = openAngleIdx; i < text.Length; i++)
        {
            if (text[i] == '<')
                depth++;
            else if (text[i] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    content = text[start..i];
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TrySplitFirstDepthZeroComma(string args, out string first, out string rest)
    {
        first = string.Empty;
        rest = string.Empty;

        int depth = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == '<')
                depth++;
            else if (args[i] == '>')
                depth--;
            else if (args[i] == ',' && depth == 0)
            {
                first = args[..i];
                rest = args[(i + 1)..];
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildCollectionPlaceholder(string? typeName, out string placeholder)
    {
        placeholder = string.Empty;
        var normalized = NormalizeTypeName(typeName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (TryExtractArrayElementType(normalized, out var arrayElementType))
        {
            var (first, second) = BuildTwoSeedValues(arrayElementType);
            placeholder = $"new {arrayElementType}[] {{ {first}, {second} }}";
            return true;
        }

        if (TryExtractGenericElementType(normalized, "List", out var listElementType)
            || TryExtractGenericElementType(normalized, "System.Collections.Generic.List", out listElementType))
        {
            var (first, second) = BuildTwoSeedValues(listElementType);
            placeholder =
                $"new global::System.Collections.Generic.List<{listElementType}> {{ {first}, {second} }}";
            return true;
        }

        if (TryExtractGenericElementType(normalized, "IEnumerable", out var enumerableElementType)
            || TryExtractGenericElementType(normalized, "System.Collections.Generic.IEnumerable", out enumerableElementType))
        {
            var (first, second) = BuildTwoSeedValues(enumerableElementType);
            placeholder = $"new {enumerableElementType}[] {{ {first}, {second} }}";
            return true;
        }

        if (TryExtractGenericElementType(normalized, "IReadOnlyCollection", out var readonlyCollectionElementType)
            || TryExtractGenericElementType(normalized, "System.Collections.Generic.IReadOnlyCollection", out readonlyCollectionElementType)
            || TryExtractGenericElementType(normalized, "IReadOnlyList", out readonlyCollectionElementType)
            || TryExtractGenericElementType(normalized, "System.Collections.Generic.IReadOnlyList", out readonlyCollectionElementType))
        {
            var (first, second) = BuildTwoSeedValues(readonlyCollectionElementType);
            placeholder =
                $"new global::System.Collections.Generic.List<{readonlyCollectionElementType}> {{ {first}, {second} }}";
            return true;
        }

        if (TryExtractGenericElementType(normalized, "ISet", out var setElementType)
            || TryExtractGenericElementType(normalized, "System.Collections.Generic.ISet", out setElementType)
            || TryExtractGenericElementType(normalized, "HashSet", out setElementType)
            || TryExtractGenericElementType(normalized, "System.Collections.Generic.HashSet", out setElementType))
        {
            var (first, second) = BuildTwoSeedValues(setElementType);
            placeholder =
                $"new global::System.Collections.Generic.HashSet<{setElementType}> {{ {first}, {second} }}";
            return true;
        }

        return false;
    }

    private static bool TryBuildScalarPlaceholder(string? typeName, out string placeholder)
    {
        placeholder = string.Empty;
        var normalized = NormalizeTypeName(typeName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var isNullable = normalized.EndsWith("?", StringComparison.Ordinal);
        var coreType = isNullable ? normalized[..^1] : normalized;

        switch (coreType)
        {
            case "string":
            case "String":
            case "System.String":
                placeholder = "\"qlstub0\"";
                return true;

            case "bool":
            case "Boolean":
            case "System.Boolean":
                placeholder = "true";
                return true;

            case "byte":
            case "Byte":
            case "System.Byte":
                placeholder = "1";
                return true;

            case "short":
            case "Int16":
            case "System.Int16":
                placeholder = "1";
                return true;

            case "int":
            case "Int32":
            case "System.Int32":
                placeholder = "1";
                return true;

            case "long":
            case "Int64":
            case "System.Int64":
                placeholder = "1L";
                return true;

            case "float":
            case "Single":
            case "System.Single":
                placeholder = "1.0f";
                return true;

            case "double":
            case "Double":
            case "System.Double":
                placeholder = "1.0d";
                return true;

            case "decimal":
            case "Decimal":
            case "System.Decimal":
                placeholder = "1.0m";
                return true;

            case "Guid":
            case "System.Guid":
                placeholder = "global::System.Guid.Parse(\"11111111-1111-1111-1111-111111111111\")";
                return true;

            case "DateTime":
            case "System.DateTime":
                placeholder = "global::System.DateTime.UtcNow";
                return true;

            case "DateOnly":
            case "System.DateOnly":
                placeholder = "global::System.DateOnly.FromDateTime(global::System.DateTime.UtcNow)";
                return true;

            case "TimeOnly":
            case "System.TimeOnly":
                placeholder = "global::System.TimeOnly.FromDateTime(global::System.DateTime.UtcNow)";
                return true;

            case "CancellationToken":
            case "System.Threading.CancellationToken":
                placeholder = "global::System.Threading.CancellationToken.None";
                return true;

            case "TimeSpan":
            case "System.TimeSpan":
                placeholder = "global::System.TimeSpan.Zero";
                return true;

            case "char":
            case "Char":
            case "System.Char":
                placeholder = "'a'";
                return true;

            case "sbyte":
            case "SByte":
            case "System.SByte":
                placeholder = "(sbyte)1";
                return true;

            case "ushort":
            case "UInt16":
            case "System.UInt16":
                placeholder = "(ushort)1";
                return true;

            case "uint":
            case "UInt32":
            case "System.UInt32":
                placeholder = "1u";
                return true;

            case "ulong":
            case "UInt64":
            case "System.UInt64":
                placeholder = "1ul";
                return true;

            case "nint":
            case "IntPtr":
            case "System.IntPtr":
                placeholder = "(nint)1";
                return true;

            case "nuint":
            case "UIntPtr":
            case "System.UIntPtr":
                placeholder = "(nuint)1";
                return true;

            default:
                return false;
        }
    }

    private static (string First, string Second) BuildTwoSeedValues(string elementTypeName)
    {
        var normalizedElementType = NormalizeTypeName(elementTypeName);

        if (IsStringTypeName(normalizedElementType))
        {
            return ("\"qlstub0\"", "\"qlstub1\"");
        }

        return normalizedElementType switch
        {
            "bool" or "Boolean" or "System.Boolean" => ("true", "false"),
            "byte" or "Byte" or "System.Byte" => ("1", "2"),
            "short" or "Int16" or "System.Int16" => ("1", "2"),
            "int" or "Int32" or "System.Int32" => ("1", "2"),
            "long" or "Int64" or "System.Int64" => ("1L", "2L"),
            "float" or "Single" or "System.Single" => ("1.0f", "2.0f"),
            "double" or "Double" or "System.Double" => ("1.0d", "2.0d"),
            "decimal" or "Decimal" or "System.Decimal" => ("1.0m", "2.0m"),
            "Guid" or "System.Guid"
                =>
                    (
                        "global::System.Guid.Parse(\"11111111-1111-1111-1111-111111111111\")",
                        "global::System.Guid.Parse(\"22222222-2222-2222-2222-222222222222\")"
                    ),
            "DateTime" or "System.DateTime"
                =>
                    (
                        "global::System.DateTime.UtcNow",
                        "global::System.DateTime.UtcNow.AddDays(1)"
                    ),
            "DateOnly" or "System.DateOnly"
                =>
                    (
                        "global::System.DateOnly.FromDateTime(global::System.DateTime.UtcNow)",
                        "global::System.DateOnly.FromDateTime(global::System.DateTime.UtcNow.AddDays(1))"
                    ),
            "TimeOnly" or "System.TimeOnly"
                =>
                    (
                        "global::System.TimeOnly.FromDateTime(global::System.DateTime.UtcNow)",
                        "global::System.TimeOnly.FromDateTime(global::System.DateTime.UtcNow.AddMinutes(1))"
                    ),
            _ => BuildEnumAwareFallbackSeedValues(elementTypeName),
        };
    }

    private static (string First, string Second) BuildEnumAwareFallbackSeedValues(string typeName)
    {
        // Prefer concrete enum members when possible to avoid translating Contains(default(enum))
        // into provider-specific degenerate SQL shapes. For non-enum types, this safely falls
        // back to default(T) at runtime.
        var first =
            $"(typeof({typeName}).IsEnum ? ({typeName})global::System.Enum.GetValues(typeof({typeName})).GetValue(0)! : default({typeName}))";
        var second =
            $"(typeof({typeName}).IsEnum ? ({typeName})global::System.Enum.GetValues(typeof({typeName})).GetValue(global::System.Enum.GetValues(typeof({typeName})).Length > 1 ? 1 : 0)! : default({typeName}))";

        return (first, second);
    }

    private static bool TryExtractArrayElementType(string normalizedTypeName, out string elementTypeName)
    {
        elementTypeName = string.Empty;
        if (!normalizedTypeName.EndsWith("[]", StringComparison.Ordinal))
        {
            return false;
        }

        elementTypeName = normalizedTypeName[..^2].Trim();
        return !string.IsNullOrWhiteSpace(elementTypeName);
    }

    private static bool TryExtractGenericElementType(
        string normalizedTypeName,
        string genericTypeName,
        out string elementTypeName)
    {
        elementTypeName = string.Empty;
        var prefix = genericTypeName + "<";
        if (!normalizedTypeName.StartsWith(prefix, StringComparison.Ordinal)
            || !normalizedTypeName.EndsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        elementTypeName = normalizedTypeName[prefix.Length..^1].Trim();
        return !string.IsNullOrWhiteSpace(elementTypeName);
    }

    private static string NormalizeTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return string.Empty;
        }

        return typeName
            .Replace("global::", string.Empty, StringComparison.Ordinal)
            .Replace('+', '.')
            .Trim();
    }

    private static bool IsStringTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var normalized = NormalizeTypeName(typeName);
        return string.Equals(normalized, "string", StringComparison.Ordinal)
               || string.Equals(normalized, "string?", StringComparison.Ordinal)
               || string.Equals(normalized, "String", StringComparison.Ordinal)
               || string.Equals(normalized, "String?", StringComparison.Ordinal)
               || string.Equals(normalized, "System.String", StringComparison.Ordinal)
               || string.Equals(normalized, "System.String?", StringComparison.Ordinal);
    }
}
