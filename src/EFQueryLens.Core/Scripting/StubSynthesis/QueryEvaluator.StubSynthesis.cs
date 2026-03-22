using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    // Stub generation and type inference helpers extracted from QueryEvaluator.cs
    // to keep EvaluateAsync flow readable.

    private static string BuildStubDeclaration(
        string name, string? rootId, TranslationRequest request, Type dbContextType)
    {
        if (!string.IsNullOrWhiteSpace(rootId)
            && string.Equals(name, rootId, StringComparison.Ordinal)
            && !string.Equals(name, request.ContextVariableName, StringComparison.Ordinal))
            return $"var {name} = {request.ContextVariableName};";

        // Use LSP-provided authoritative type when available — skip heuristics entirely.
        if (request.LocalVariableTypes.TryGetValue(name, out var knownTypeName)
            && !string.IsNullOrWhiteSpace(knownTypeName))
            return BuildStubFromTypeName(knownTypeName, name);

        // Gridify placeholders must win over generic member-access synthesis.
        // `query` is commonly used both as IGridifyQuery and as `query.Page` / `query.PageSize`.
        // If we synthesize it as anonymous object first, extension calls fail with CS1503.
        if (TryBuildGridifyStubDeclaration(name, request.Expression, dbContextType, out var gridifyStub))
            return gridifyStub;

        var memberTypes = InferMemberAccessTypes(name, request.Expression, dbContextType, request.UsingAliases);
        if (memberTypes.Count > 0)
        {
            var memberInitializers = string.Join(
                ", ",
                memberTypes.Select(kvp =>
                    $"{kvp.Key} = {BuildScalarPlaceholderExpression(kvp.Value)}"));

            return $"var {name} = new {{ {memberInitializers} }};";
        }

        var inferred = InferVariableType(name, request.Expression, dbContextType);
        inferred ??= InferMethodArgumentType(name, request.Expression, dbContextType);
        inferred ??= InferComparisonOperandType(name, request.Expression, dbContextType);
        if (inferred is not null)
        {
            var tn = ToCSharpTypeName(inferred);
            var value = BuildScalarPlaceholderExpression(inferred);
            return $"{tn} {name} = {value};";
        }

        if (LooksLikeBooleanConditionIdentifier(name, request.Expression))
            return $"bool {name} = true;";

        if (LooksLikeNumericArithmeticIdentifier(name, request.Expression))
            return $"int {name} = 1;";

        var elem = InferContainsElementType(name, request.Expression, dbContextType);
        if (elem is not null)
        {
            var en = ToCSharpTypeName(elem);
            var containsValues = BuildContainsPlaceholderValues(elem);
            return $"System.Collections.Generic.List<{en}> {name} = new() {{ {containsValues} }};";
        }

        var sel = InferSelectEntityType(name, request.Expression, dbContextType);
        if (sel is not null)
        {
            var sn = ToCSharpTypeName(sel);
            return $"System.Linq.Expressions.Expression<System.Func<{sn}, object>> {name} = _ => default!;";
        }

        var whereEntity = InferWhereEntityType(name, request.Expression, dbContextType);
        if (whereEntity is not null)
        {
            var wn = ToCSharpTypeName(whereEntity);
            return $"System.Linq.Expressions.Expression<System.Func<{wn}, bool>> {name} = _ => true;";
        }

        if (LooksLikeCancellationTokenArgument(name, request.Expression))
            return $"System.Threading.CancellationToken {name} = default;";

        return $"object {name} = default;";
    }

    private static string BuildStubFromTypeName(string typeName, string varName)
    {
        return typeName.Trim() switch
        {
            "int" or "Int32" or "System.Int32" => $"int {varName} = 0;",
            "long" or "Int64" or "System.Int64" => $"long {varName} = 0L;",
            "short" or "Int16" or "System.Int16" => $"short {varName} = 0;",
            "byte" or "Byte" or "System.Byte" => $"byte {varName} = 0;",
            "uint" or "UInt32" or "System.UInt32" => $"uint {varName} = 0u;",
            "ulong" or "UInt64" or "System.UInt64" => $"ulong {varName} = 0ul;",
            "bool" or "Boolean" or "System.Boolean" => $"bool {varName} = false;",
            "string" or "String" or "System.String" => $"string {varName} = \"\";",
            "char" or "Char" or "System.Char" => $"char {varName} = '\\0';",
            "decimal" or "Decimal" or "System.Decimal" => $"decimal {varName} = 0m;",
            "double" or "Double" or "System.Double" => $"double {varName} = 0.0;",
            "float" or "Single" or "System.Single" => $"float {varName} = 0.0f;",
            "Guid" or "System.Guid" => $"System.Guid {varName} = System.Guid.Empty;",
            "DateTime" or "System.DateTime" => $"System.DateTime {varName} = System.DateTime.UtcNow;",
            "DateTimeOffset" or "System.DateTimeOffset" => $"System.DateTimeOffset {varName} = System.DateTimeOffset.UtcNow;",
            "DateOnly" or "System.DateOnly" => $"System.DateOnly {varName} = System.DateOnly.FromDateTime(System.DateTime.Today);",
            "TimeOnly" or "System.TimeOnly" => $"System.TimeOnly {varName} = System.TimeOnly.MinValue;",
            "CancellationToken" or "System.Threading.CancellationToken" => $"System.Threading.CancellationToken {varName} = default;",
            var tn when tn.EndsWith("[]", StringComparison.Ordinal)
                => $"{tn[..^2]}[] {varName} = System.Array.Empty<{tn[..^2]}>();",
            var tn when TryExtractCollectionElementType(tn, out var elem)
                => $"System.Collections.Generic.List<{elem}> {varName} = new() {{ default({elem}) }};",
            // Expression<Func<...>> — generate a typed lambda rather than GetUninitializedObject.
            // An uninitialized Expression has null internal nodes (Body, Parameters, etc.);
            // EF Core walks the expression tree to produce SQL and will throw on any null node.
            // A proper lambda compiles to a valid expression tree that EF Core can translate:
            //   predicate (bool return)  → _ => true   (matches all rows — safe for WHERE)
            //   projection (other return) → _ => default! (typed null — best-effort for SELECT)
            var tn when IsExpressionFuncTypeName(tn)
                => IsBoolPredicateExpression(tn)
                    ? $"{tn} {varName} = _ => true;"
                    : $"{tn} {varName} = _ => default!;",
            // Unknown complex type (user-defined DTO, entity, etc.).
            // Use GetUninitializedObject so the instance is non-null: EF Core must be able to
            // evaluate captured parameter expressions (e.g. model.PlanningCaseId) at runtime,
            // and a null reference throws before SQL is ever produced.
            // Strip nullable-reference-type annotation ('?') — typeof() has no CLR distinction for ref types.
            var tn => $"var {varName} = ({tn.TrimEnd('?')})global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof({tn.TrimEnd('?')}));",
        };
    }

    private static bool TryExtractCollectionElementType(string typeName, out string elementType)
    {
        elementType = string.Empty;
        var lt = typeName.IndexOf('<');
        var gt = typeName.LastIndexOf('>');
        if (lt < 0 || gt < 0 || gt <= lt) return false;

        var outer = typeName[..lt].Trim();
        if (outer is not ("List" or "IList" or "ICollection" or "IEnumerable"
            or "IReadOnlyList" or "IReadOnlyCollection" or "ISet" or "HashSet"
            or "System.Collections.Generic.List" or "System.Collections.Generic.IList"
            or "System.Collections.Generic.IEnumerable" or "System.Collections.Generic.IReadOnlyList"))
            return false;

        var inner = typeName[(lt + 1)..gt].Trim();
        if (string.IsNullOrWhiteSpace(inner)) return false;

        elementType = inner;
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="typeName"/> is an <c>Expression&lt;Func&lt;...&gt;&gt;</c>
    /// type, either with or without the full <c>System.Linq.Expressions</c> namespace prefix.
    /// </summary>
    private static bool IsExpressionFuncTypeName(string typeName) =>
        typeName.Contains("Expression<", StringComparison.Ordinal)
        && typeName.Contains("Func<", StringComparison.Ordinal);

    /// <summary>
    /// Returns true when the <c>Func&lt;&gt;</c>'s return type is <c>bool</c> — i.e. the
    /// expression is a predicate suitable for <c>Where</c> / <c>Any</c> / <c>Count</c>.
    /// Detects by checking that the full type name ends with <c>, bool&gt;&gt;</c>
    /// (the inner <c>&gt;</c> closes <c>Func&lt;</c>, the outer closes <c>Expression&lt;</c>).
    /// </summary>
    private static bool IsBoolPredicateExpression(string typeName)
    {
        var t = typeName.TrimEnd('?');
        return t.EndsWith(", bool>>", StringComparison.Ordinal)
            || t.EndsWith(",bool>>", StringComparison.Ordinal)
            || t.EndsWith(", bool?>>", StringComparison.Ordinal);
    }

}
