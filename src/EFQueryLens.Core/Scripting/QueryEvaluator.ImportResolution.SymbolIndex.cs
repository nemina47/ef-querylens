using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EFQueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
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
