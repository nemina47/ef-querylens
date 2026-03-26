using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
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

    private static Type? InferMethodArgumentType(string variableName, string expression, Type dbContextType)
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

        foreach (var invocation in parsed.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetInvocationMethodName(invocation);
            if (string.IsNullOrWhiteSpace(methodName))
                continue;

            var argumentIndex = invocation.ArgumentList.Arguments
                .Select((arg, index) => new { arg, index })
                .Where(x => TryGetSimpleIdentifier(x.arg.Expression, out var id)
                            && string.Equals(id, variableName, StringComparison.Ordinal))
                .Select(x => x.index)
                .DefaultIfEmpty(-1)
                .Max();

            if (argumentIndex < 0)
                continue;

            if (string.Equals(methodName, "IsNullOrEmpty", StringComparison.Ordinal)
                || string.Equals(methodName, "IsNullOrWhiteSpace", StringComparison.Ordinal))
            {
                return typeof(string);
            }

            if (!string.Equals(methodName, "Contains", StringComparison.Ordinal)
                && !string.Equals(methodName, "StartsWith", StringComparison.Ordinal)
                && !string.Equals(methodName, "EndsWith", StringComparison.Ordinal)
                && !string.Equals(methodName, "Equals", StringComparison.Ordinal))
            {
                var fromSignature = TryInferTypeFromMethodSignature(methodName, argumentIndex, dbContextType);
                if (fromSignature is not null)
                    return fromSignature;
                continue;
            }

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (LooksLikeStringExpression(memberAccess.Expression, dbContextType))
                return typeof(string);
        }

        return null;
    }

    private static Type? InferComparisonOperandType(string variableName, string expression, Type dbContextType)
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

        foreach (var binary in parsed.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>())
        {
            if (!IsComparisonOperator(binary.Kind()))
                continue;

            if (TryGetSimpleIdentifier(binary.Left, out var leftIdentifier)
                && string.Equals(leftIdentifier, variableName, StringComparison.Ordinal))
            {
                var inferredFromRight = InferOperandType(binary.Right, dbContextType);
                if (inferredFromRight is not null)
                    return inferredFromRight;
            }

            if (TryGetSimpleIdentifier(binary.Right, out var rightIdentifier)
                && string.Equals(rightIdentifier, variableName, StringComparison.Ordinal))
            {
                var inferredFromLeft = InferOperandType(binary.Left, dbContextType);
                if (inferredFromLeft is not null)
                    return inferredFromLeft;
            }
        }

        return null;
    }

    private static bool IsComparisonOperator(SyntaxKind kind)
    {
        return kind is SyntaxKind.EqualsExpression
            or SyntaxKind.NotEqualsExpression
            or SyntaxKind.GreaterThanExpression
            or SyntaxKind.GreaterThanOrEqualExpression
            or SyntaxKind.LessThanExpression
            or SyntaxKind.LessThanOrEqualExpression;
    }

    private static Type? InferOperandType(ExpressionSyntax operand, Type dbContextType)
    {
        switch (operand)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return InferOperandType(parenthesized.Expression, dbContextType);

            case CastExpressionSyntax cast:
                return InferOperandType(cast.Expression, dbContextType);

            case LiteralExpressionSyntax literal:
                return literal.Token.Value?.GetType();

            case PrefixUnaryExpressionSyntax prefix
                 when prefix.Kind() == Microsoft.CodeAnalysis.CSharp.SyntaxKind.UnaryMinusExpression
                     || prefix.Kind() == Microsoft.CodeAnalysis.CSharp.SyntaxKind.UnaryPlusExpression:
                return InferOperandType(prefix.Operand, dbContextType);

            case MemberAccessExpressionSyntax memberAccess:
                return FindEntityPropertyType(dbContextType, memberAccess.Name.Identifier.ValueText);

            case InvocationExpressionSyntax invocation
                when invocation.Expression is MemberAccessExpressionSyntax invokedMember:
            {
                var method = invokedMember.Name.Identifier.ValueText;
                if (string.Equals(method, "Count", StringComparison.Ordinal)
                    || string.Equals(method, "CountAsync", StringComparison.Ordinal)
                    || string.Equals(method, "Length", StringComparison.Ordinal))
                {
                    return typeof(int);
                }

                if (string.Equals(method, "LongCount", StringComparison.Ordinal)
                    || string.Equals(method, "LongCountAsync", StringComparison.Ordinal))
                {
                    return typeof(long);
                }

                return null;
            }

            default:
                return null;
        }
    }

    private static Type? TryInferTypeFromMethodSignature(
        string methodName, int argumentIndex, Type dbContextType)
    {
        var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(dbContextType.Assembly);
        var assemblies = (IEnumerable<System.Reflection.Assembly>?)alc?.Assemblies
            ?? AppDomain.CurrentDomain.GetAssemblies();

        var candidates = new HashSet<Type>();

        foreach (var asm in assemblies)
        {
            if (IsRuntimeOrFrameworkAssembly(asm))
                continue;

            foreach (var type in SafeGetTypes(asm))
            {
                foreach (var method in type.GetMethods(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                        continue;

                    var parameters = method.GetParameters();
                    if (argumentIndex >= parameters.Length)
                        continue;

                    var paramType = parameters[argumentIndex].ParameterType;
                    if (paramType.IsGenericParameter || paramType == typeof(object))
                        continue;

                    candidates.Add(paramType);
                }
            }
        }

        return candidates.Count == 1 ? candidates.First() : null;
    }

    private static bool IsRuntimeOrFrameworkAssembly(System.Reflection.Assembly asm)
    {
        var name = asm.GetName().Name ?? string.Empty;
        return name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
            || name == "mscorlib"
            || name == "netstandard"
            || name == "System"
            || name == "Microsoft.CSharp";
    }

    private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null).Select(t => t!);
        }
        catch { return []; }
    }

    private static bool LooksLikeStringExpression(ExpressionSyntax expression, Type dbContextType)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return LooksLikeStringExpression(parenthesized.Expression, dbContextType);

            case CastExpressionSyntax cast:
                return LooksLikeStringExpression(cast.Expression, dbContextType);

            case LiteralExpressionSyntax literal:
                return literal.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression;

            case InterpolatedStringExpressionSyntax:
                return true;

            case InvocationExpressionSyntax invocation
                when invocation.Expression is MemberAccessExpressionSyntax memberInvocation:
            {
                var method = memberInvocation.Name.Identifier.ValueText;
                if (string.Equals(method, "ToLower", StringComparison.Ordinal)
                    || string.Equals(method, "ToUpper", StringComparison.Ordinal)
                    || string.Equals(method, "Trim", StringComparison.Ordinal)
                    || string.Equals(method, "TrimStart", StringComparison.Ordinal)
                    || string.Equals(method, "TrimEnd", StringComparison.Ordinal)
                    || string.Equals(method, "Substring", StringComparison.Ordinal)
                    || string.Equals(method, "Replace", StringComparison.Ordinal)
                    || string.Equals(method, "PadLeft", StringComparison.Ordinal)
                    || string.Equals(method, "PadRight", StringComparison.Ordinal))
                {
                    return LooksLikeStringExpression(memberInvocation.Expression, dbContextType);
                }

                return false;
            }

            case MemberAccessExpressionSyntax memberAccess:
            {
                var memberName = memberAccess.Name.Identifier.ValueText;
                var propType = FindEntityPropertyType(dbContextType, memberName);
                if (propType == typeof(string))
                    return true;

                return LooksLikeStringExpression(memberAccess.Expression, dbContextType);
            }

            default:
                return false;
        }
    }
}
