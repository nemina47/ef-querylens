using System.Text.RegularExpressions;

namespace QueryLens.Core.Scripting;

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

        var memberTypes = InferMemberAccessTypes(name, request.Expression, dbContextType);
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

    private static IReadOnlyDictionary<string, Type> InferMemberAccessTypes(string variableName, string expression, Type dbContextType)
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
            var inferred = InferMemberTypeFromComparison(variableName, member, expression, dbContextType)
                ?? FindEntityPropertyType(dbContextType, member)
                ?? InferMemberTypeFromNameHeuristic(member);

            if (inferred is not null)
                result[member] = inferred;
        }

        return result;
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
