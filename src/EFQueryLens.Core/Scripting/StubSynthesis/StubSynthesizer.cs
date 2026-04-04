using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Text.RegularExpressions;

namespace EFQueryLens.Core.Scripting.Evaluation;

internal static partial class StubSynthesizer
{
    // Stub generation and type inference helpers extracted from QueryEvaluator.cs
    // to keep EvaluateAsync flow readable.

    internal static List<string> BuildInitialStubs(TranslationRequest request, Type dbContextType)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (dbContextType is null)
            throw new ArgumentNullException(nameof(dbContextType));

        var stubs = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var rootId = ImportResolver.TryExtractRootIdentifier(request.Expression);
        var graph = request.LocalSymbolGraph;
        var referencedNames = CollectReferencedIdentifierNames(request.Expression);

        foreach (var hint in graph.OrderBy(h => h.DeclarationOrder))
        {
            if (string.IsNullOrWhiteSpace(hint.Name))
                continue;
            if (seenNames.Contains(hint.Name))
                continue;
            if (string.Equals(hint.Name, request.ContextVariableName, StringComparison.Ordinal))
                continue;
            if (request.UseAsyncRunner
                && IsCancellationTokenTypeName(hint.TypeName)
                && string.Equals(hint.Name, "ct", StringComparison.Ordinal))
                continue;
            if (IsAnonymousTypeName(hint.TypeName)
                && !referencedNames.Contains(hint.Name))
                continue;

            var stub = BuildStubDeclaration(hint.Name, rootId, request, dbContextType);
            if (string.IsNullOrWhiteSpace(stub))
                continue;

            stubs.Add(stub);
            seenNames.Add(hint.Name);
        }

        return stubs;
    }

    private static bool IsAnonymousTypeName(string? typeName) =>
        !string.IsNullOrWhiteSpace(typeName)
        && typeName.Contains("<anonymous type:", StringComparison.Ordinal);

    private static HashSet<string> CollectReferencedIdentifierNames(string expression)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(expression))
            return names;

        try
        {
            var parsed = SyntaxFactory.ParseExpression(expression);
            foreach (var identifier in parsed.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var name = identifier.Identifier.ValueText;
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        catch
        {
            // Best effort only.
        }

        return names;
    }

    private static bool IsCancellationTokenTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var normalized = typeName.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
        return string.Equals(normalized, "System.Threading.CancellationToken", StringComparison.Ordinal)
               || string.Equals(normalized, "CancellationToken", StringComparison.Ordinal);
    }

    internal static string BuildStubDeclaration(
        string name, string? rootId, TranslationRequest request, Type dbContextType)
    {
        if (!string.IsNullOrWhiteSpace(rootId)
            && string.Equals(name, rootId, StringComparison.Ordinal)
            && !string.Equals(name, request.ContextVariableName, StringComparison.Ordinal))
            return $"var {name} = {request.ContextVariableName};";

        var localHint = request.LocalSymbolGraph
            .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.Ordinal));

        var hintedTypeName = localHint?.TypeName;
        if (request.UseAsyncRunner
            && IsCancellationTokenTypeName(hintedTypeName)
            && !string.Equals(name, "ct", StringComparison.Ordinal))
        {
            return $"System.Threading.CancellationToken {name} = ct;";
        }

        if (string.Equals(localHint?.ReplayPolicy, LocalSymbolReplayPolicies.ReplayInitializer, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(localHint?.InitializerExpression))
        {
            var initializerStub = BuildStubFromInitializer(
                name,
                hintedTypeName,
                localHint!.InitializerExpression!,
                dbContextType,
                request.UsingAliases);

            if (!string.IsNullOrWhiteSpace(initializerStub))
                return initializerStub;
        }

        if (!string.IsNullOrWhiteSpace(hintedTypeName))
        {
            var hintedStub = BuildStubFromTypeName(hintedTypeName!, name, dbContextType, request.UsingAliases);
            if (!string.IsNullOrWhiteSpace(hintedStub))
                return hintedStub;
        }

        // Strict semantic mode only: if LSP did not provide a deterministic type/member hint
        // for this symbol, let compile diagnostics surface the missing symbol.
        return string.Empty;
    }

    internal static string BuildStubFromTypeName(
        string typeName,
        string varName,
        Type dbContextType,
        IReadOnlyDictionary<string, string>? usingAliases = null)
    {
        // Guard against unresolved/unknown types represented as "?" or similar markers.
        var normalizedTypeName = typeName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTypeName) || normalizedTypeName == "?")
            return string.Empty;
        var comparableTypeName = normalizedTypeName.Replace("global::", string.Empty, StringComparison.Ordinal);

        var resolvedType = TryResolveStubType(normalizedTypeName, dbContextType, usingAliases);
        if (IsStaticClassType(resolvedType))
            return string.Empty;

        var isNullableValueTypeSyntax = normalizedTypeName.EndsWith("?", StringComparison.Ordinal)
            && resolvedType is not null
            && resolvedType.IsValueType;
        if (isNullableValueTypeSyntax)
        {
            var underlying = ToCSharpTypeName(resolvedType!);
            return $"{underlying}? {varName} = {BuildScalarPlaceholderExpression(resolvedType!)};";
        }

        if (string.Equals(comparableTypeName, "Gridify.IGridifyQuery", StringComparison.Ordinal))
        {
            return $"{comparableTypeName} {varName} = new global::Gridify.GridifyQuery();";
        }

        if (comparableTypeName.StartsWith("Gridify.IGridifyMapper<", StringComparison.Ordinal))
        {
            return $"{comparableTypeName} {varName} = null!;";
        }

        return comparableTypeName switch
        {
            "int" or "Int32" or "System.Int32" => $"int {varName} = 0;",
            "long" or "Int64" or "System.Int64" => $"long {varName} = 0L;",
            "short" or "Int16" or "System.Int16" => $"short {varName} = 0;",
            "byte" or "Byte" or "System.Byte" => $"byte {varName} = 0;",
            "uint" or "UInt32" or "System.UInt32" => $"uint {varName} = 0u;",
            "ulong" or "UInt64" or "System.UInt64" => $"ulong {varName} = 0ul;",
            "bool" or "Boolean" or "System.Boolean" => $"bool {varName} = false;",
            // Include nullable reference-type variants — the CLR makes no distinction
            // for reference types, and 'string ""' satisfies both 'string' and 'string?'.
            "string" or "String" or "System.String"
                or "string?" or "String?" or "System.String?" => $"string {varName} = \"\";",
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
                => $"{tn[..^2]}[] {varName} = new {tn[..^2]}[] {{ default({tn[..^2]}), default({tn[..^2]}) }};",
            _ when TryExtractCollectionElementType(normalizedTypeName, out var elem)
                => $"System.Collections.Generic.List<{elem}> {varName} = new() {{ default({elem}), default({elem}) }};",
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
            var _ => BuildUninitializedObjectStub(normalizedTypeName, varName, resolvedType),
        };
    }

    private static string BuildUninitializedObjectStub(string typeName, string varName, Type? resolvedType)
    {
        if (resolvedType?.IsInterface == true)
        {
            var interfaceTypeName = ToCSharpTypeName(resolvedType).TrimEnd('?');
            return $"var {varName} = __CreateInterfaceProxy__<{interfaceTypeName}>();";
        }

        var targetTypeName = resolvedType is not null
            ? ToCSharpTypeName(resolvedType).TrimEnd('?')
            : typeName.TrimEnd('?');

        return $"var {varName} = ({targetTypeName})global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof({targetTypeName}));";
    }

    private static Type? TryResolveStubType(
        string typeName,
        Type dbContextType,
        IReadOnlyDictionary<string, string>? usingAliases)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var normalized = typeName.Trim().TrimEnd('?');
        normalized = normalized.Replace("global::", string.Empty, StringComparison.Ordinal);

        if (TryResolveKeywordAliasType(normalized, out var aliasType))
            return aliasType;

        var aliases = usingAliases ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var expanded = ExpandAlias(normalized, aliases);

        var resolved = ResolveTypeFromName(expanded, dbContextType, aliases);
        if (resolved is not null)
            return resolved;

        resolved = Type.GetType(expanded, throwOnError: false, ignoreCase: false);
        if (resolved is not null)
            return resolved;

        if (!expanded.Contains('.', StringComparison.Ordinal))
            return Type.GetType($"System.{expanded}", throwOnError: false, ignoreCase: false);

        return null;
    }

    private static bool TryResolveKeywordAliasType(string normalizedTypeName, out Type? type)
    {
        type = normalizedTypeName switch
        {
            "bool" => typeof(bool),
            "byte" => typeof(byte),
            "sbyte" => typeof(sbyte),
            "char" => typeof(char),
            "decimal" => typeof(decimal),
            "double" => typeof(double),
            "float" => typeof(float),
            "int" => typeof(int),
            "uint" => typeof(uint),
            "long" => typeof(long),
            "ulong" => typeof(ulong),
            "short" => typeof(short),
            "ushort" => typeof(ushort),
            "string" => typeof(string),
            "object" => typeof(object),
            _ => null,
        };

        return type is not null;
    }

    private static Type? ResolveTypeFromName(
        string typeName,
        Type dbContextType,
        IReadOnlyDictionary<string, string> usingAliases)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var expanded = ExpandAlias(typeName.Trim(), usingAliases);

        var direct = dbContextType.Assembly.GetType(expanded, throwOnError: false, ignoreCase: false);
        if (direct is not null)
            return direct;

        if (expanded.Contains('.', StringComparison.Ordinal))
            return null;

        var fullNameSuffix = $".{expanded}";
        try
        {
            return dbContextType.Assembly
                .GetTypes()
                .FirstOrDefault(t =>
                    string.Equals(t.Name, expanded, StringComparison.Ordinal)
                    || (t.FullName is not null && t.FullName.EndsWith(fullNameSuffix, StringComparison.Ordinal)));
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types
                .Where(t => t is not null)
                .Select(t => t!)
                .FirstOrDefault(t =>
                    string.Equals(t.Name, expanded, StringComparison.Ordinal)
                    || (t.FullName is not null && t.FullName.EndsWith(fullNameSuffix, StringComparison.Ordinal)));
        }
    }

    private static string ExpandAlias(string typeName, IReadOnlyDictionary<string, string> usingAliases)
    {
        if (usingAliases.Count == 0)
            return typeName;

        if (usingAliases.TryGetValue(typeName, out var exactAlias) && !string.IsNullOrWhiteSpace(exactAlias))
            return exactAlias;

        var dotIndex = typeName.IndexOf('.');
        if (dotIndex <= 0)
            return typeName;

        var alias = typeName[..dotIndex];
        if (!usingAliases.TryGetValue(alias, out var aliasExpansion) || string.IsNullOrWhiteSpace(aliasExpansion))
            return typeName;

        return aliasExpansion + typeName[dotIndex..];
    }

    private static bool IsStaticClassType(Type? type)
        => type is not null && type.IsAbstract && type.IsSealed && !type.IsValueType;

    private static bool TryExtractCollectionElementType(string typeName, out string elementType)
    {
        elementType = string.Empty;
        var normalizedTypeName = typeName.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
        var lt = normalizedTypeName.IndexOf('<');
        var gt = normalizedTypeName.LastIndexOf('>');
        if (lt < 0 || gt < 0 || gt <= lt) return false;

        var outer = normalizedTypeName[..lt].Trim();
        if (outer is not ("List" or "IList" or "ICollection" or "IEnumerable"
            or "IReadOnlyList" or "IReadOnlyCollection" or "ISet" or "HashSet"
            or "System.Collections.Generic.List" or "System.Collections.Generic.IList"
            or "System.Collections.Generic.IEnumerable" or "System.Collections.Generic.IReadOnlyList"))
            return false;

        var inner = normalizedTypeName[(lt + 1)..gt].Trim();
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

    private static string BuildStubFromInitializer(
        string variableName,
        string? hintedTypeName,
        string initializerExpression,
        Type dbContextType,
        IReadOnlyDictionary<string, string>? usingAliases)
    {
        if (string.IsNullOrWhiteSpace(initializerExpression))
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(hintedTypeName)
            && TryExtractCollectionElementType(hintedTypeName.Trim(), out var elementTypeName))
        {
            initializerExpression = QualifyTypeChains(initializerExpression, elementTypeName);
        }

        // Initializers like `request.CreatedAfterUtc.Value` are branch-local and only
        // safe under guards that are not preserved in the extracted query fragment.
        // Replaying them in the eval scaffold can throw before EF translation starts.
        if (IsUnsafeInitializerReplay(initializerExpression)
            && !string.IsNullOrWhiteSpace(hintedTypeName)
            && !string.Equals(hintedTypeName, "?", StringComparison.Ordinal))
        {
            return BuildStubFromTypeName(hintedTypeName.Trim(), variableName, dbContextType, usingAliases);
        }

        if (RequiresTargetType(initializerExpression)
            && !string.IsNullOrWhiteSpace(hintedTypeName)
            && !string.Equals(hintedTypeName, "?", StringComparison.Ordinal))
        {
            var normalizedTypeName = hintedTypeName.Trim();
            var resolvedType = TryResolveStubType(normalizedTypeName, dbContextType, usingAliases);
            if (IsStaticClassType(resolvedType))
                return string.Empty;

            return $"{normalizedTypeName} {variableName} = {initializerExpression};";
        }

        return $"var {variableName} = {initializerExpression};";
    }

    private static string QualifyTypeChains(string expression, string fullyQualifiedTypeName)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(fullyQualifiedTypeName))
            return expression;

        var replacementTypeName = fullyQualifiedTypeName.Trim();
        var replacementIsNullable = replacementTypeName.EndsWith("?", StringComparison.Ordinal);
        var replacementNonNullable = replacementTypeName.TrimEnd('?');

        var simpleTypeName = replacementNonNullable;
        var genericTickIndex = simpleTypeName.IndexOf('<');
        if (genericTickIndex >= 0)
            simpleTypeName = simpleTypeName[..genericTickIndex];
        var lastDot = simpleTypeName.LastIndexOf('.');
        if (lastDot >= 0 && lastDot + 1 < simpleTypeName.Length)
            simpleTypeName = simpleTypeName[(lastDot + 1)..];
        simpleTypeName = simpleTypeName.Replace("global::", string.Empty, StringComparison.Ordinal).TrimEnd('?');
        if (string.IsNullOrWhiteSpace(simpleTypeName))
            return expression;

        // Normalize chains like Domain.Enums.MyType or Foo.Bar.MyType to the
        // deterministic hinted type to avoid namespace/type-shadowing ambiguities.
        // Preserve nullable marker from the original source (if present) and avoid
        // introducing null-conditional type forms like `MyType?.Member`.
        var pattern = $@"\b(?:[A-Za-z_]\w*\.)+{Regex.Escape(simpleTypeName)}(?<nullable>\?)?";
        return Regex.Replace(
            expression,
            pattern,
            match =>
            {
                var nullableSuffix = match.Groups["nullable"].Success && replacementIsNullable ? "?" : string.Empty;
                return replacementNonNullable + nullableSuffix;
            },
            RegexOptions.CultureInvariant);
    }

    private static bool RequiresTargetType(string initializerExpression)
    {
        var trimmed = initializerExpression.Trim();
        if (string.Equals(trimmed, "default", StringComparison.Ordinal)
            || string.Equals(trimmed, "new()", StringComparison.Ordinal))
        {
            return true;
        }

        // Collection expressions (`[...]`) require target typing.
        // In some parser/language-version combinations ParseExpression may not
        // surface CollectionExpressionSyntax reliably, so keep a lexical guard.
        if (trimmed.StartsWith("[", StringComparison.Ordinal)
            && trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var parsed = SyntaxFactory.ParseExpression(trimmed);
            return parsed is CollectionExpressionSyntax;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnsafeInitializerReplay(string initializerExpression)
    {
        if (initializerExpression.Contains(".Value", StringComparison.Ordinal))
            return true;

        ExpressionSyntax parsed;
        try
        {
            parsed = SyntaxFactory.ParseExpression(initializerExpression);
        }
        catch
        {
            return false;
        }

        foreach (var invocation in parsed.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            // Static calls such as Math.Max(...) are safe to replay.
            if (memberAccess.Expression is IdentifierNameSyntax identifier
                && identifier.Identifier.ValueText.Length > 0
                && char.IsUpper(identifier.Identifier.ValueText[0]))
            {
                continue;
            }

            return true;
        }

        return false;
    }

}
