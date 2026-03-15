using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
    private static IReadOnlyList<string> InferMissingExtensionStaticImports(
        IEnumerable<Diagnostic> errors,
        IEnumerable<Assembly> assemblies)
    {
        var requested = new List<(string ReceiverType, string MethodName)>();
        foreach (var error in errors.Where(e => e.Id == "CS1061"))
        {
            if (!TryParseMissingExtensionDiagnostic(error.GetMessage(), out var receiverType, out var methodName))
                continue;

            if (requested.Any(r => string.Equals(r.ReceiverType, receiverType, StringComparison.Ordinal)
                                   && string.Equals(r.MethodName, methodName, StringComparison.Ordinal)))
            {
                continue;
            }

            requested.Add((receiverType, methodName));
        }

        if (requested.Count == 0)
            return [];

        var imports = new HashSet<string>(StringComparer.Ordinal);

        foreach (var asm in assemblies)
        {
            Type[] allTypes;
            try
            {
                allTypes = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                allTypes = rtle.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var type in allTypes)
            {
                if (!(type.IsAbstract && type.IsSealed))
                    continue;

                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                }
                catch
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    if (!method.IsDefined(typeof(ExtensionAttribute), inherit: false))
                        continue;

                    var request = requested.FirstOrDefault(r => string.Equals(r.MethodName, method.Name, StringComparison.Ordinal));
                    if (request == default)
                        continue;

                    var firstParam = method.GetParameters().FirstOrDefault()?.ParameterType;
                    if (firstParam is null || !IsReceiverNameMatch(firstParam, request.ReceiverType))
                        continue;

                    if (!string.IsNullOrWhiteSpace(type.FullName))
                        imports.Add(type.FullName.Replace('+', '.'));
                }
            }
        }

        return imports.ToArray();
    }

    private static bool TryParseMissingExtensionDiagnostic(
        string message,
        out string receiverType,
        out string methodName)
    {
        receiverType = string.Empty;
        methodName = string.Empty;

        var match = Regex.Match(
            message,
            @"^'(?<receiver>[^']+)' does not contain a definition for '(?<method>[^']+)'.*first argument of type '(?<arg>[^']+)'.*$");

        if (!match.Success)
            return false;

        methodName = match.Groups["method"].Value;
        receiverType = match.Groups["arg"].Success
            ? match.Groups["arg"].Value
            : match.Groups["receiver"].Value;

        return !string.IsNullOrWhiteSpace(receiverType) && !string.IsNullOrWhiteSpace(methodName);
    }

    private static bool IsReceiverNameMatch(Type parameterType, string receiverTypeName)
    {
        if (parameterType.IsByRef)
            parameterType = parameterType.GetElementType() ?? parameterType;

        if (string.Equals(parameterType.Name, receiverTypeName, StringComparison.Ordinal)
            || string.Equals(parameterType.FullName, receiverTypeName, StringComparison.Ordinal))
        {
            return true;
        }

        if (parameterType.IsGenericType)
        {
            var genericName = parameterType.GetGenericTypeDefinition().Name;
            var tick = genericName.IndexOf('`');
            if (tick > 0)
            {
                genericName = genericName[..tick];
            }

            if (string.Equals(genericName, receiverTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryExtractRootIdentifier(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        try
        {
            var parsed = SyntaxFactory.ParseExpression(expression);
            var current = parsed;

            while (true)
            {
                switch (current)
                {
                    case InvocationExpressionSyntax invocation
                        when invocation.Expression is MemberAccessExpressionSyntax memberAccess:
                        current = memberAccess.Expression;
                        continue;

                    case MemberAccessExpressionSyntax member:
                        current = member.Expression;
                        continue;

                    case ParenthesizedExpressionSyntax parenthesized:
                        current = parenthesized.Expression;
                        continue;

                    case CastExpressionSyntax cast:
                        current = cast.Expression;
                        continue;

                    case IdentifierNameSyntax identifier:
                        return identifier.Identifier.ValueText;

                    default:
                        return null;
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool TryNormalizeRootContextHopFromErrors(
        IReadOnlyList<Diagnostic> errors,
        string expression,
        Type dbContextType,
        out string normalizedExpression)
    {
        normalizedExpression = expression;

        var rootId = TryExtractRootIdentifier(expression);
        if (string.IsNullOrWhiteSpace(rootId))
            return false;

        if (!TryExtractLeadingHop(expression, rootId, out var hopName, out var nextMember))
            return false;

        // If DbContext already has this hop, it is not a wrapper hop.
        if (dbContextType.GetProperty(hopName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null)
            return false;

        // If the member after the hop is not on DbContext, removing the hop is likely incorrect.
        if (dbContextType.GetProperty(nextMember, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is null)
            return false;

        var hasMatchingError = errors.Any(d =>
            d.Id == "CS1061"
            && d.GetMessage().Contains($"'{dbContextType.Name}'", StringComparison.Ordinal)
            && d.GetMessage().Contains($"'{hopName}'", StringComparison.Ordinal));

        if (!hasMatchingError)
            return false;

        var pattern = $@"(?<!\w){Regex.Escape(rootId)}\s*\.\s*{Regex.Escape(hopName)}\s*\.";
        normalizedExpression = Regex.Replace(expression, pattern, rootId + ".");
        return !string.Equals(normalizedExpression, expression, StringComparison.Ordinal);
    }

    private static bool TryExtractLeadingHop(
        string expression,
        string rootId,
        out string hopName,
        out string nextMember)
    {
        hopName = string.Empty;
        nextMember = string.Empty;

        ExpressionSyntax parsed;
        try
        {
            parsed = SyntaxFactory.ParseExpression(expression);
        }
        catch
        {
            return false;
        }

        var members = new List<string>();
        var current = parsed;

        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation
                    when invocation.Expression is MemberAccessExpressionSyntax memberAccess:
                    members.Add(memberAccess.Name.Identifier.ValueText);
                    current = memberAccess.Expression;
                    continue;

                case MemberAccessExpressionSyntax member:
                    members.Add(member.Name.Identifier.ValueText);
                    current = member.Expression;
                    continue;

                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case CastExpressionSyntax cast:
                    current = cast.Expression;
                    continue;

                case IdentifierNameSyntax identifier:
                    if (!string.Equals(identifier.Identifier.ValueText, rootId, StringComparison.Ordinal)
                        || members.Count < 2)
                    {
                        return false;
                    }

                    // members were collected from right-to-left; reverse lookup for root->... order.
                    hopName = members[^1];
                    nextMember = members[^2];
                    return true;

                default:
                    return false;
            }
        }
    }

    private static bool IsUnsupportedTopLevelMethodInvocation(string expression, string ctxVar)
    {
        var m = Regex.Match(expression,
            @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(");
        if (!m.Success)
            return false;

        if (string.Equals(m.Groups[1].Value, ctxVar, StringComparison.Ordinal)
            && string.Equals(m.Groups[2].Value, "Set", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

}
