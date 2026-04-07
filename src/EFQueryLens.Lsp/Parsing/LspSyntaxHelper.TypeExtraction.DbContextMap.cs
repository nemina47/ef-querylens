// Metadata-only DbContext/DbSet member-type extraction for LSP hover rewriting.
// Uses MetadataLoadContext so QueryLens can inspect project outputs without
// pinning user bin-folder assemblies in the default load context.
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    private static IReadOnlyDictionary<(string Receiver, string Member), string> BuildLambdaParameterMemberTypeMap(
        ExpressionSyntax expression,
        string contextVariableName,
        string? targetAssemblyPath,
        string? dbContextTypeName,
        Action<string>? debugLog = null)
    {
        var result = new Dictionary<(string Receiver, string Member), string>();

        try
        {
            var dbSetName = expression.DescendantNodesAndSelf()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(ma => ma.Expression is IdentifierNameSyntax id
                             && string.Equals(id.Identifier.ValueText, contextVariableName, StringComparison.Ordinal))
                .Select(ma => ma.Name.Identifier.ValueText)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

            if (string.IsNullOrWhiteSpace(dbSetName))
            {
                debugLog?.Invoke("lambda-map-skip reason=no-dbset-root");
                return result;
            }

            if (!TryResolveDbSetEntityMemberTypes(targetAssemblyPath, dbContextTypeName, dbSetName!, out var memberTypes))
            {
                debugLog?.Invoke($"lambda-map-skip reason=dbset-entity-types-unresolved dbset={dbSetName} dbContext={dbContextTypeName}");
                return result;
            }

            foreach (var memberAccess in expression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
            {
                if (memberAccess.Expression is not IdentifierNameSyntax receiverIdentifier)
                    continue;

                var receiverName = receiverIdentifier.Identifier.ValueText;
                if (string.IsNullOrWhiteSpace(receiverName))
                    continue;

                var memberName = memberAccess.Name.Identifier.ValueText;
                if (string.IsNullOrWhiteSpace(memberName))
                    continue;

                if (!memberTypes.TryGetValue(memberName, out var memberTypeName))
                    continue;

                result[(receiverName, memberName)] = memberTypeName;
            }

            if (result.Count > 0)
            {
                debugLog?.Invoke(
                    $"lambda-map-built dbset={dbSetName} count={result.Count} keys={string.Join(",", result.Keys.Select(k => $"{k.Receiver}.{k.Member}"))}");
            }
            else
            {
                debugLog?.Invoke($"lambda-map-empty dbset={dbSetName}");
            }
        }
        catch (Exception ex)
        {
            debugLog?.Invoke($"lambda-map-error type={ex.GetType().Name} message={ex.Message}");
        }

        return result;
    }

    private static bool TryResolveDbSetEntityMemberTypes(
        string? targetAssemblyPath,
        string? dbContextTypeName,
        string dbSetName,
        out IReadOnlyDictionary<string, string> memberTypes)
    {
        memberTypes = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(targetAssemblyPath) || !File.Exists(targetAssemblyPath))
            return false;

        try
        {
            var assemblyDir = Path.GetDirectoryName(targetAssemblyPath) ?? string.Empty;
            var userDlls = Directory.Exists(assemblyDir)
                ? Directory.GetFiles(assemblyDir, "*.dll", SearchOption.TopDirectoryOnly)
                : (string[])[targetAssemblyPath];
            var runtimeDlls = Directory.GetFiles(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll", SearchOption.TopDirectoryOnly);

            var resolver = new PathAssemblyResolver(userDlls.Concat(runtimeDlls).Distinct().ToArray());
            using var mlc = new MetadataLoadContext(resolver);

            Assembly assembly;
            try { assembly = mlc.LoadFromAssemblyPath(targetAssemblyPath); }
            catch { return false; }

            var dbContextType = ResolveDbContextType(mlc, assembly, assemblyDir, dbContextTypeName);
            if (dbContextType is null)
                return false;

            var dbSetProperty = dbContextType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p =>
                    string.Equals(p.Name, dbSetName, StringComparison.Ordinal)
                    && p.PropertyType.IsGenericType
                    && string.Equals(
                        p.PropertyType.GetGenericTypeDefinition().FullName,
                        "Microsoft.EntityFrameworkCore.DbSet`1",
                        StringComparison.Ordinal));
            if (dbSetProperty is null)
                return false;

            var entityType = dbSetProperty.PropertyType.GetGenericArguments()[0];
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var typeName = ToDeterministicTypeNameFromSystemType(prop.PropertyType);
                if (!string.IsNullOrWhiteSpace(typeName))
                    map[prop.Name] = typeName!;
            }

            foreach (var field in entityType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var typeName = ToDeterministicTypeNameFromSystemType(field.FieldType);
                if (!string.IsNullOrWhiteSpace(typeName))
                    map[field.Name] = typeName!;
            }

            memberTypes = map;
            return map.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static Type? ResolveDbContextType(MetadataLoadContext mlc, Assembly assembly, string assemblyDir, string? dbContextTypeName)
    {
        static string NormalizeTypeLookupName(string value)
            => value.Trim()
                .TrimEnd('?')
                .Replace("global::", string.Empty, StringComparison.Ordinal)
                .Trim();

        static bool IsDbContextType(Type t)
        {
            for (var current = t; current is not null;)
            {
                if (string.Equals(current.FullName, "Microsoft.EntityFrameworkCore.DbContext", StringComparison.Ordinal)
                    || string.Equals(current.Name, "DbContext", StringComparison.Ordinal))
                {
                    return true;
                }

                try { current = current.BaseType; }
                catch { break; }
            }

            return false;
        }

        var allTypes = new List<Type>();
        allTypes.AddRange(GetLoadableTypes(assembly));

        if (Directory.Exists(assemblyDir))
        {
            foreach (var dll in Directory.EnumerateFiles(assemblyDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var loaded = mlc.LoadFromAssemblyPath(dll);
                    allTypes.AddRange(GetLoadableTypes(loaded));
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        var distinctTypes = allTypes
            .Where(static t => t is not null)
            .DistinctBy(static t => t.AssemblyQualifiedName ?? t.FullName ?? t.Name)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(dbContextTypeName))
        {
            var normalized = NormalizeTypeLookupName(dbContextTypeName);
            var simpleName = normalized;
            var dotIndex = simpleName.LastIndexOf('.');
            if (dotIndex >= 0 && dotIndex < simpleName.Length - 1)
                simpleName = simpleName[(dotIndex + 1)..];

            var direct = distinctTypes.FirstOrDefault(t =>
                             string.Equals(
                                 NormalizeTypeLookupName(t.FullName ?? string.Empty),
                                 normalized,
                                 StringComparison.Ordinal))
                         ?? distinctTypes.FirstOrDefault(t =>
                             string.Equals(
                                 NormalizeTypeLookupName(t.Name),
                                 simpleName,
                                 StringComparison.Ordinal));
            if (direct is not null && IsDbContextType(direct))
                return direct;
        }

        return distinctTypes.FirstOrDefault(IsDbContextType);
    }

    private static IReadOnlyList<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string? ToDeterministicTypeNameFromSystemType(Type type)
    {
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            var genericDefName = genericDef.FullName;
            if (string.IsNullOrWhiteSpace(genericDefName))
                return null;

            var tickIndex = genericDefName.IndexOf('`');
            var trimmed = tickIndex >= 0 ? genericDefName[..tickIndex] : genericDefName;
            var args = string.Join(", ", type.GetGenericArguments()
                .Select(arg => ToDeterministicTypeNameFromSystemType(arg) ?? "global::System.Object"));
            return $"global::{trimmed}<{args}>";
        }

        var fullName = type.FullName;
        return string.IsNullOrWhiteSpace(fullName) ? null : $"global::{fullName}";
    }
}
