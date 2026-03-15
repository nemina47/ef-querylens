using System.Reflection;
using System.Runtime.Loader;

namespace EFQueryLens.Core;

public sealed partial class QueryLensEngine
{
    // Reflection helpers
    private static MethodInfo? FindMethod(Type type, string methodName) =>
        type.GetMethod(methodName)
        ?? type.GetInterfaces()
            .SelectMany(i => i.GetMethods())
            .FirstOrDefault(m => m.Name == methodName);

    private static string? GetTableName(object entityType)
    {
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(entityType.GetType().Assembly);
            var relAsm = (alc?.Assemblies ?? Enumerable.Empty<Assembly>())
                .Concat(AssemblyLoadContext.Default.Assemblies)
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.EntityFrameworkCore.Relational");

            var extType = relAsm?.GetType(
                "Microsoft.EntityFrameworkCore.RelationalEntityTypeExtensions");
            var method = extType?.GetMethod("GetTableName",
                BindingFlags.Public | BindingFlags.Static);

            return method?.Invoke(null, [entityType]) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetColumnName(object property)
    {
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(property.GetType().Assembly);
            var relAsm = (alc?.Assemblies ?? Enumerable.Empty<Assembly>())
                .Concat(AssemblyLoadContext.Default.Assemblies)
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.EntityFrameworkCore.Relational");

            var extType = relAsm?.GetType(
                "Microsoft.EntityFrameworkCore.RelationalPropertyExtensions");

            var method = extType?
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "GetColumnName" &&
                    m.GetParameters().Length == 1);

            return method?.Invoke(null, [property]) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsKey(object property, Type entityTypeType, object entityType)
    {
        try
        {
            var getKeys = FindMethod(entityTypeType, "GetKeys");
            if (getKeys is null)
            {
                return false;
            }

            var keys = (System.Collections.IEnumerable)getKeys.Invoke(entityType, null)!;
            var propName = (string)(property.GetType().GetProperty("Name")?.GetValue(property) ?? "");

            foreach (var key in keys)
            {
                var keyProps = key.GetType().GetProperty("Properties")?.GetValue(key);
                if (keyProps is System.Collections.IEnumerable kpEnum)
                {
                    foreach (var kp in kpEnum)
                    {
                        var kpName = (string)(kp.GetType().GetProperty("Name")?.GetValue(kp) ?? "");
                        if (kpName == propName)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
