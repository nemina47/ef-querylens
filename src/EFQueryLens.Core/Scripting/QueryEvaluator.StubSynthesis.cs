using System.Text.RegularExpressions;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Runtime.Loader;

namespace EFQueryLens.Core.Scripting;

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

    private static bool TryBuildGridifyStubDeclaration(
        string variableName,
        string expression,
        Type dbContextType,
        out string declaration)
    {
        declaration = string.Empty;

        if (!TryGetGridifyArgumentRole(variableName, expression, out var role, out var sourceExpression))
            return false;

        if (role == GridifyArgumentRole.Query)
        {
            declaration = $"global::Gridify.IGridifyQuery {variableName} = new global::Gridify.GridifyQuery();";
            return true;
        }

        if (role != GridifyArgumentRole.Mapper || sourceExpression is null)
            return false;

        var entityType = InferQueryEntityTypeFromSource(sourceExpression, dbContextType);
        if (entityType is null)
            return false;

        declaration = $"global::Gridify.IGridifyMapper<{ToCSharpTypeName(entityType)}>? {variableName} = null;";
        return true;
    }

    private static bool TryGetGridifyArgumentRole(
        string variableName,
        string expression,
        out GridifyArgumentRole role,
        out ExpressionSyntax? sourceExpression)
    {
        role = GridifyArgumentRole.None;
        sourceExpression = null;

        if (!expression.Contains("ApplyFilteringAndOrdering", StringComparison.Ordinal))
            return false;

        var parsed = SyntaxFactory.ParseExpression(expression);
        foreach (var invocation in parsed.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetInvocationMethodName(invocation);
            if (!string.Equals(methodName, "ApplyFilteringAndOrdering", StringComparison.Ordinal))
                continue;

            var isExtensionCall = invocation.Expression is MemberAccessExpressionSyntax;
            var args = invocation.ArgumentList.Arguments;

            var queryArgIndex = isExtensionCall ? 0 : 1;
            var mapperArgIndex = isExtensionCall ? 1 : 2;

            if (args.Count <= queryArgIndex)
                continue;

            if (TryGetSimpleIdentifier(args[queryArgIndex].Expression, out var queryArg)
                && string.Equals(queryArg, variableName, StringComparison.Ordinal))
            {
                role = GridifyArgumentRole.Query;
                sourceExpression = isExtensionCall
                    ? ((MemberAccessExpressionSyntax)invocation.Expression).Expression
                    : args[0].Expression;
                return true;
            }

            if (args.Count <= mapperArgIndex)
                continue;

            if (TryGetSimpleIdentifier(args[mapperArgIndex].Expression, out var mapperArg)
                && string.Equals(mapperArg, variableName, StringComparison.Ordinal))
            {
                role = GridifyArgumentRole.Mapper;
                sourceExpression = isExtensionCall
                    ? ((MemberAccessExpressionSyntax)invocation.Expression).Expression
                    : args[0].Expression;
                return true;
            }
        }

        return false;
    }

    private static string? GetInvocationMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => null,
        };
    }

    private static bool TryGetSimpleIdentifier(ExpressionSyntax expression, out string identifier)
    {
        switch (expression)
        {
            case IdentifierNameSyntax id:
                identifier = id.Identifier.ValueText;
                return true;

            case PostfixUnaryExpressionSyntax postfix
                when postfix.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.SuppressNullableWarningExpression:
                return TryGetSimpleIdentifier(postfix.Operand, out identifier);

            case ParenthesizedExpressionSyntax parenthesized:
                return TryGetSimpleIdentifier(parenthesized.Expression, out identifier);

            case CastExpressionSyntax cast:
                return TryGetSimpleIdentifier(cast.Expression, out identifier);

            default:
                identifier = string.Empty;
                return false;
        }
    }

    private static Type? InferQueryEntityTypeFromSource(ExpressionSyntax sourceExpression, Type dbContextType)
    {
        ExpressionSyntax current = sourceExpression;

        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation
                    when invocation.Expression is MemberAccessExpressionSyntax memberAccess:
                    current = memberAccess.Expression;
                    continue;

                case MemberAccessExpressionSyntax member:
                    var prop = dbContextType.GetProperty(member.Name.Identifier.ValueText);
                    if (prop is not null)
                    {
                        var elementType = TryGetQueryableElementType(prop.PropertyType);
                        if (elementType is not null)
                            return elementType;
                    }

                    current = member.Expression;
                    continue;

                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case CastExpressionSyntax cast:
                    current = cast.Expression;
                    continue;

                default:
                    return null;
            }
        }
    }

    private static Type? TryGetQueryableElementType(Type queryableType)
    {
        if (queryableType.IsGenericType && queryableType.GetGenericTypeDefinition() == typeof(IQueryable<>))
            return queryableType.GetGenericArguments()[0];

        var iqueryable = queryableType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>));
        if (iqueryable is not null)
            return iqueryable.GetGenericArguments()[0];

        if (queryableType.IsGenericType && queryableType.GetGenericArguments().Length == 1)
            return queryableType.GetGenericArguments()[0];

        return null;
    }

    private enum GridifyArgumentRole
    {
        None,
        Query,
        Mapper,
    }

    private static bool LooksLikeTypeOrNamespacePrefix(
        string id, string expression, IReadOnlyDictionary<string, string> aliases)
    {
        if (aliases.ContainsKey(id)) return true;
        if (string.IsNullOrWhiteSpace(id) || !char.IsUpper(id[0])) return false;
        return Regex.IsMatch(expression, $@"(?<!\w){Regex.Escape(id)}\s*\.\s*[A-Z_]");
    }

    private static Type? InferVariableType(string v, string expr, Type ctx)
    {
        var pattern =
            $@"\.(\w+)\s*(?:==|!=|>|<|>=|<=)\s*{Regex.Escape(v)}(?!\w)"
            + "|"
            + $@"(?<!\w){Regex.Escape(v)}\s*(?:==|!=|>|<|>=|<=)\s*\w+\.(\w+)";
        var m = Regex.Match(expr, pattern);
        if (!m.Success) return null;
        return FindEntityPropertyType(ctx, m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    private static Type? InferContainsElementType(string v, string expr, Type ctx)
    {
        var m = Regex.Match(expr, $@"(?<!\w){Regex.Escape(v)}\s*\.\s*Contains\s*\(\s*\w+\s*\.\s*(\w+)");
        return m.Success ? FindEntityPropertyType(ctx, m.Groups[1].Value) : null;
    }

    private static Type? InferSelectEntityType(string v, string expr, Type ctx)
    {
        if (!Regex.IsMatch(expr, $@"\.\s*Select\s*\(\s*{Regex.Escape(v)}\s*\)")) return null;
        var m = Regex.Match(expr, @"^\s*[A-Za-z_][A-Za-z0-9_]*\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)");
        if (!m.Success) return null;
        var prop = ctx.GetProperty(m.Groups[1].Value);
        return prop?.PropertyType.IsGenericType == true
            ? prop.PropertyType.GetGenericArguments().FirstOrDefault() : null;
    }

    private static Type? InferWhereEntityType(string v, string expr, Type ctx)
    {
        if (!Regex.IsMatch(expr, $@"\.\s*Where\s*\(\s*{Regex.Escape(v)}\s*\)")) return null;
        var m = Regex.Match(expr, @"^\s*[A-Za-z_][A-Za-z0-9_]*\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)");
        if (!m.Success) return null;
        var prop = ctx.GetProperty(m.Groups[1].Value);
        return prop?.PropertyType.IsGenericType == true
            ? prop.PropertyType.GetGenericArguments().FirstOrDefault() : null;
    }

    private static IReadOnlyDictionary<string, Type> InferMemberAccessTypes(
        string variableName,
        string expression,
        Type dbContextType,
        IReadOnlyDictionary<string, string> usingAliases)
    {
        var members = Regex.Matches(
            expression,
            $@"(?<!\w){Regex.Escape(variableName)}\.(\w+)")
            .Cast<Match>()
            // Ignore method calls like userIds.Contains(...); synthesize only property-style member access.
            .Where(m => !IsInvokedMemberAccess(m, expression))
            .Select(m => m.Groups[1].Value)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (members.Count == 0)
            return new Dictionary<string, Type>(StringComparer.Ordinal);

        var result = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var member in members)
        {
            var inferred = InferMemberTypeFromIsPattern(variableName, member, expression, dbContextType, usingAliases)
                ?? InferMemberTypeFromComparison(variableName, member, expression, dbContextType)
                ?? FindEntityPropertyType(dbContextType, member)
                ?? InferMemberTypeFromNameHeuristic(member);

            if (inferred is not null)
                result[member] = inferred;
        }

        return result;
    }

    private static Type? InferMemberTypeFromIsPattern(
        string variableName,
        string memberName,
        string expression,
        Type dbContextType,
        IReadOnlyDictionary<string, string> usingAliases)
    {
        ExpressionSyntax parsed;
        try
        {
            parsed = SyntaxFactory.ParseExpression(expression);
        }
        catch
        {
            return null;
        }

        foreach (var isPattern in parsed.DescendantNodesAndSelf().OfType<IsPatternExpressionSyntax>())
        {
            if (!IsTargetMemberAccess(isPattern.Expression, variableName, memberName))
                continue;

            var typeName = TryExtractTypeNameFromPattern(isPattern.Pattern);
            if (string.IsNullOrWhiteSpace(typeName))
                continue;

            var resolved = ResolveTypeFromName(typeName!, dbContextType, usingAliases);
            if (resolved?.IsEnum == true)
                return resolved;
        }

        return null;
    }

    private static bool IsTargetMemberAccess(ExpressionSyntax expression, string variableName, string memberName)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return IsTargetMemberAccess(parenthesized.Expression, variableName, memberName);

            case CastExpressionSyntax cast:
                return IsTargetMemberAccess(cast.Expression, variableName, memberName);

            case MemberAccessExpressionSyntax memberAccess:
                return memberAccess.Expression is IdentifierNameSyntax identifier
                       && string.Equals(identifier.Identifier.ValueText, variableName, StringComparison.Ordinal)
                       && string.Equals(memberAccess.Name.Identifier.ValueText, memberName, StringComparison.Ordinal);

            default:
                return false;
        }
    }

    private static string? TryExtractTypeNameFromPattern(PatternSyntax pattern)
    {
        switch (pattern)
        {
            case ConstantPatternSyntax constantPattern:
                return TryExtractTypeNameFromConstantPattern(constantPattern.Expression);

            case BinaryPatternSyntax binaryPattern:
                return TryExtractTypeNameFromPattern(binaryPattern.Left)
                    ?? TryExtractTypeNameFromPattern(binaryPattern.Right);

            case ParenthesizedPatternSyntax parenthesizedPattern:
                return TryExtractTypeNameFromPattern(parenthesizedPattern.Pattern);

            case DeclarationPatternSyntax declarationPattern:
                return declarationPattern.Type.ToString();

            case TypePatternSyntax typePattern:
                return typePattern.Type.ToString();

            default:
                return null;
        }
    }

    private static string? TryExtractTypeNameFromConstantPattern(ExpressionSyntax constantExpression)
    {
        switch (constantExpression)
        {
            case MemberAccessExpressionSyntax memberAccess:
                // EnumValue pattern: Namespace.EnumType.Member -> return Namespace.EnumType
                return memberAccess.Expression.ToString();

            case ParenthesizedExpressionSyntax parenthesized:
                return TryExtractTypeNameFromConstantPattern(parenthesized.Expression);

            case CastExpressionSyntax cast:
                return cast.Type.ToString();

            default:
                return null;
        }
    }

    private static Type? ResolveTypeFromName(
        string typeName,
        Type dbContextType,
        IReadOnlyDictionary<string, string> usingAliases)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var expanded = ExpandAliasTypeName(typeName, usingAliases);
        if (string.IsNullOrWhiteSpace(expanded))
            return null;

        expanded = expanded!.Replace("global::", string.Empty, StringComparison.Ordinal);

        var alc = AssemblyLoadContext.GetLoadContext(dbContextType.Assembly);
        var assemblies = (alc?.Assemblies ?? [dbContextType.Assembly]).Distinct().ToArray();

        foreach (var asm in assemblies)
        {
            var direct = asm.GetType(expanded, throwOnError: false, ignoreCase: false);
            if (direct is not null)
                return direct;
        }

        var simpleName = expanded.Contains('.') ? expanded[(expanded.LastIndexOf('.') + 1)..] : expanded;
        foreach (var asm in assemblies)
        {
            try
            {
                var matched = asm.GetTypes().FirstOrDefault(t =>
                    string.Equals(t.Name, simpleName, StringComparison.Ordinal)
                    || string.Equals(t.FullName, expanded, StringComparison.Ordinal));
                if (matched is not null)
                    return matched;
            }
            catch (ReflectionTypeLoadException rtle)
            {
                var matched = rtle.Types
                    .Where(t => t is not null)
                    .FirstOrDefault(t =>
                        string.Equals(t!.Name, simpleName, StringComparison.Ordinal)
                        || string.Equals(t.FullName, expanded, StringComparison.Ordinal));
                if (matched is not null)
                    return matched!;
            }
            catch
            {
            }
        }

        return null;
    }

    private static string? ExpandAliasTypeName(string typeName, IReadOnlyDictionary<string, string> usingAliases)
    {
        if (string.IsNullOrWhiteSpace(typeName) || usingAliases.Count == 0)
            return typeName;

        foreach (var kvp in usingAliases)
        {
            if (string.Equals(typeName, kvp.Key, StringComparison.Ordinal))
                return kvp.Value;

            var prefix = kvp.Key + ".";
            if (typeName.StartsWith(prefix, StringComparison.Ordinal))
                return kvp.Value + typeName[prefix.Length..];
        }

        return typeName;
    }

    private static bool IsInvokedMemberAccess(Match match, string expression)
    {
        var index = match.Index + match.Length;
        while (index < expression.Length && char.IsWhiteSpace(expression[index]))
        {
            index++;
        }

        return index < expression.Length && expression[index] == '(';
    }

    private static Type? InferMemberTypeFromComparison(string variableName, string memberName, string expression, Type dbContextType)
    {
        var pattern =
            $@"\.(\w+)\s*(?:==|!=|>|<|>=|<=)\s*{Regex.Escape(variableName)}\.{Regex.Escape(memberName)}(?!\w)"
            + "|"
            + $@"(?<!\w){Regex.Escape(variableName)}\.{Regex.Escape(memberName)}\s*(?:==|!=|>|<|>=|<=)\s*\w+\.(\w+)";

        var match = Regex.Match(expression, pattern);
        if (!match.Success)
            return null;

        var entityProperty = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return FindEntityPropertyType(dbContextType, entityProperty);
    }

    private static Type? InferMemberTypeFromNameHeuristic(string memberName)
    {
        if (string.Equals(memberName, "Now", StringComparison.Ordinal)
            || string.Equals(memberName, "UtcNow", StringComparison.Ordinal)
            || string.Equals(memberName, "Today", StringComparison.Ordinal))
        {
            return typeof(DateTime);
        }

        if (memberName.EndsWith("Id", StringComparison.Ordinal))
            return typeof(Guid);

        return typeof(string);
    }

    private static bool LooksLikeCancellationTokenArgument(string v, string expr)
    {
        if (Regex.IsMatch(expr, $@"\w+Async\s*\([^\)]*\b{Regex.Escape(v)}\b[^\)]*\)")) return true;
        return v.Equals("ct", StringComparison.OrdinalIgnoreCase)
            || v.Equals("cancellationToken", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBooleanConditionIdentifier(string variableName, string expression)
    {
        var id = Regex.Escape(variableName);

        // Handles conditions like: isIntranetUser || ...  and  ... && isIntranetUser
        if (Regex.IsMatch(expression, $@"(?<!\w){id}(?!\w)\s*(\|\||&&)"))
            return true;

        if (Regex.IsMatch(expression, $@"(\|\||&&)\s*!?\s*(?<!\w){id}(?!\w)"))
            return true;

        // Handles unary negation: !isIntranetUser
        if (Regex.IsMatch(expression, $@"!\s*(?<!\w){id}(?!\w)"))
            return true;

        // Handles ternary condition usage: isIntranetUser ? x : y
        if (Regex.IsMatch(expression, $@"(?<!\w){id}(?!\w)\s*\?"))
            return true;

        return false;
    }

    private static bool LooksLikeNumericArithmeticIdentifier(string variableName, string expression)
    {
        var id = Regex.Escape(variableName);

        if (Regex.IsMatch(expression, $@"(?<!\w){id}(?!\w)\s*[*+\-/]"))
            return true;

        if (Regex.IsMatch(expression, $@"[*+\-/]\s*(?<!\w){id}(?!\w)"))
            return true;

        if (Regex.IsMatch(expression, $@"\.(Skip|Take)\s*\(\s*(?<!\w){id}(?!\w)\s*\)", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private static string BuildContainsPlaceholderValues(Type elementType)
    {
        var t = Nullable.GetUnderlyingType(elementType) ?? elementType;

        if (t == typeof(Guid))
            return "System.Guid.Empty, new System.Guid(\"00000000-0000-0000-0000-000000000001\")";
        if (t == typeof(string))
            return "\"__ql_stub_0\", \"__ql_stub_1\"";
        if (t == typeof(bool))
            return "false, true";
        if (t == typeof(char))
            return "'a', 'b'";
        if (t == typeof(decimal))
            return "0m, 1m";
        if (t == typeof(double))
            return "0d, 1d";
        if (t == typeof(float))
            return "0f, 1f";
        if (t == typeof(long))
            return "0L, 1L";
        if (t == typeof(ulong))
            return "0UL, 1UL";
        if (t == typeof(int))
            return "0, 1";
        if (t == typeof(uint))
            return "0U, 1U";
        if (t == typeof(short))
            return "(short)0, (short)1";
        if (t == typeof(ushort))
            return "(ushort)0, (ushort)1";
        if (t == typeof(byte))
            return "(byte)0, (byte)1";
        if (t == typeof(sbyte))
            return "(sbyte)0, (sbyte)1";
        if (t == typeof(DateTime))
            return "System.DateTime.UnixEpoch, System.DateTime.UnixEpoch.AddDays(1)";
        if (t.IsEnum)
        {
            var enumTypeName = ToCSharpTypeName(t);
            return $"({enumTypeName})0, ({enumTypeName})1";
        }

        var typeName = ToCSharpTypeName(elementType);
        return $"default({typeName})!, default({typeName})!";
    }

    private static string BuildScalarPlaceholderExpression(Type variableType)
    {
        var t = Nullable.GetUnderlyingType(variableType) ?? variableType;

        if (t == typeof(string))
            return "\"__ql_stub_0\"";
        if (t == typeof(Guid))
            return "new System.Guid(\"00000000-0000-0000-0000-000000000001\")";
        if (t == typeof(bool))
            return "true";
        if (t == typeof(char))
            return "'a'";
        if (t == typeof(decimal))
            return "1m";
        if (t == typeof(double))
            return "1d";
        if (t == typeof(float))
            return "1f";
        if (t == typeof(long))
            return "1L";
        if (t == typeof(ulong))
            return "1UL";
        if (t == typeof(int))
            return "1";
        if (t == typeof(uint))
            return "1U";
        if (t == typeof(short))
            return "(short)1";
        if (t == typeof(ushort))
            return "(ushort)1";
        if (t == typeof(byte))
            return "(byte)1";
        if (t == typeof(sbyte))
            return "(sbyte)1";
        if (t == typeof(DateTime))
            return "System.DateTime.UnixEpoch";
        if (t.IsEnum)
            return $"({ToCSharpTypeName(t)})1";

        var variableTypeName = ToCSharpTypeName(variableType);
        return $"default({variableTypeName})";
    }

    private static Type? FindEntityPropertyType(Type ctx, string propName)
    {
        foreach (var p in ctx.GetProperties())
        {
            if (!p.PropertyType.IsGenericType) continue;
            var ep = p.PropertyType.GetGenericArguments().FirstOrDefault()?.GetProperty(propName);
            if (ep is not null) return ep.PropertyType;
        }

        return null;
    }

    private static string ToCSharpTypeName(Type t)
    {
        if (t == typeof(void))    return "void";
        if (t == typeof(bool))    return "bool";
        if (t == typeof(byte))    return "byte";
        if (t == typeof(sbyte))   return "sbyte";
        if (t == typeof(char))    return "char";
        if (t == typeof(decimal)) return "decimal";
        if (t == typeof(double))  return "double";
        if (t == typeof(float))   return "float";
        if (t == typeof(int))     return "int";
        if (t == typeof(uint))    return "uint";
        if (t == typeof(long))    return "long";
        if (t == typeof(ulong))   return "ulong";
        if (t == typeof(object))  return "object";
        if (t == typeof(short))   return "short";
        if (t == typeof(ushort))  return "ushort";
        if (t == typeof(string))  return "string";
        if (t.IsArray) return $"{ToCSharpTypeName(t.GetElementType()!)}[]";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            return $"{ToCSharpTypeName(t.GetGenericArguments()[0])}?";
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition().FullName ?? t.Name;
            var tick = def.IndexOf('`');
            if (tick >= 0)
                def = def[..tick];

            return $"{def.Replace('+', '.')}<{string.Join(", ", t.GetGenericArguments().Select(ToCSharpTypeName))}>";
        }

        return (t.FullName ?? t.Name).Replace('+', '.');
    }
}
