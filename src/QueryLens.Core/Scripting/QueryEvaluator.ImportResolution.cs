using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace QueryLens.Core.Scripting;

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

        var m = Regex.Match(expression, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:\.|$)");
        return m.Success ? m.Groups[1].Value : null;
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

    private static (HashSet<string> Namespaces, HashSet<string> Types) BuildKnownNamespaceAndTypeIndex(
        IEnumerable<Assembly> assemblies)
    {
        var ns = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asm in assemblies)
        {
            var key = string.IsNullOrWhiteSpace(asm.Location)
                ? asm.FullName ?? Guid.NewGuid().ToString("N")
                : asm.Location;
            if (!seen.Add(key))
                continue;

            Type[] all;
            try
            {
                all = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                all = rtle.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var t in all)
            {
                if (!string.IsNullOrWhiteSpace(t.FullName))
                    types.Add(t.FullName.Replace('+', '.'));

                if (!string.IsNullOrWhiteSpace(t.Namespace))
                    AddNamespaceAndParents(t.Namespace, ns);
            }
        }

        return (ns, types);
    }

    private static void AddNamespaceAndParents(string n, ISet<string> dest)
    {
        var span = n.AsSpan();
        while (true)
        {
            dest.Add(span.ToString());
            var dot = span.LastIndexOf('.');
            if (dot <= 0)
                break;

            span = span[..dot];
        }
    }

    private static bool IsResolvableNamespace(string n, IReadOnlySet<string> ns) => ns.Contains(n);

    private static bool IsResolvableType(string n, IReadOnlySet<string> types) => types.Contains(n);

    private static bool IsResolvableTypeOrNamespace(
        string n,
        IReadOnlySet<string> ns,
        IReadOnlySet<string> types) =>
        ns.Contains(n) || types.Contains(n);

    private static bool IsValidAliasName(string a) =>
        !string.IsNullOrWhiteSpace(a) && SyntaxFacts.IsValidIdentifier(a);

    private static bool IsValidUsingName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return !CSharpSyntaxTree.ParseText($"using {name};").GetDiagnostics().Any();
    }
}
