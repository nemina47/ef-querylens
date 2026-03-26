using System.Reflection;
using System.Text.RegularExpressions;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    /// <summary>
    /// Detects <c>DbSet.Find()</c> / <c>FindAsync()</c> calls in the expression and rewrites the
    /// key arguments to <c>default(PKType)</c> values sourced from the EF Core model.
    /// Returns the rewritten expression, or <c>null</c> if the DbSet or PK cannot be resolved.
    /// </summary>
    private static string? TryRewriteFindExpression(string expression, object dbInstance)
    {
        // Match: <DbSetPropertyName>.Find( or <DbSetPropertyName>.FindAsync(
        var match = Regex.Match(expression,
            @"([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*(Find(?:Async)?)\s*\(",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        var dbSetName = match.Groups[1].Value;
        var isAsync = match.Groups[2].Value.EndsWith("Async", StringComparison.OrdinalIgnoreCase);

        // Resolve entity CLR type from the DbSet<T> property on the DbContext.
        var dbSetProp = dbInstance.GetType()
            .GetProperty(dbSetName, BindingFlags.Public | BindingFlags.Instance);
        if (dbSetProp == null)
            return null;

        var dbSetType = dbSetProp.PropertyType;
        if (!dbSetType.IsGenericType)
            return null;

        var entityClrType = dbSetType.GetGenericArguments()[0];

        // Get PK CLR types from the EF Core model via reflection (cross-ALC safe).
        var pkTypes = ResolvePrimaryKeyTypes(dbInstance, entityClrType);
        if (pkTypes.Count == 0)
            return null;

        // Build replacement arg list: default(int) [, default(Guid)] [, default(CancellationToken)]
        var defaultArgs = pkTypes.Select(t => $"default({GetCSharpTypeName(t)})").ToList();
        if (isAsync)
            defaultArgs.Add("default(global::System.Threading.CancellationToken)");

        // Replace the original argument list between Find( ... ) with the typed defaults.
        var openParenPos = match.Index + match.Length - 1;
        var closeParenPos = FindMatchingCloseParen(expression, openParenPos);
        if (closeParenPos < 0)
            return null;

        return expression[..(openParenPos + 1)]
             + string.Join(", ", defaultArgs)
             + expression[closeParenPos..];
    }

    private static List<Type> ResolvePrimaryKeyTypes(object dbInstance, Type entityClrType)
    {
        // --- EF Core model API path (handles explicit interface implementations via interface scan) ---
        try
        {
            var modelProp = dbInstance.GetType()
                .GetProperty("Model", BindingFlags.Public | BindingFlags.Instance);
            var model = modelProp?.GetValue(dbInstance);
            if (model != null)
            {
                // Scan interfaces for FindEntityType(string) — RuntimeModel uses explicit impl.
                var findEntityType = model.GetType().GetInterfaces()
                    .Select(i => i.GetMethod("FindEntityType", [typeof(string)]))
                    .FirstOrDefault(m => m != null);

                var entityTypeMeta = findEntityType?.Invoke(model, [entityClrType.FullName]);
                if (entityTypeMeta != null)
                {
                    // Scan interfaces for FindPrimaryKey().
                    var findPk = entityTypeMeta.GetType().GetInterfaces()
                        .Select(i => i.GetMethod("FindPrimaryKey"))
                        .FirstOrDefault(m => m != null);
                    var pk = findPk?.Invoke(entityTypeMeta, null);
                    if (pk != null)
                    {
                        // Properties may be on the interface too.
                        var propsProp = pk.GetType().GetInterfaces()
                            .SelectMany(i => i.GetProperties())
                            .FirstOrDefault(p => p.Name == "Properties")
                            ?? pk.GetType().GetProperty("Properties");

                        if (propsProp?.GetValue(pk) is System.Collections.IEnumerable props)
                        {
                            var result = new List<Type>();
                            foreach (var p in props)
                            {
                                var clrTypeProp = p.GetType().GetInterfaces()
                                    .SelectMany(i => i.GetProperties())
                                    .FirstOrDefault(pr => pr.Name == "ClrType")
                                    ?? p.GetType().GetProperty("ClrType");
                                if (clrTypeProp?.GetValue(p) is Type clrType)
                                    result.Add(clrType);
                            }
                            if (result.Count > 0) return result;
                        }
                    }
                }
            }
        }
        catch { /* fall through to convention */ }

        // --- Convention fallback: Id or {TypeName}Id CLR property ---
        var idProp = entityClrType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)
                  ?? entityClrType.GetProperty($"{entityClrType.Name}Id", BindingFlags.Public | BindingFlags.Instance);
        return idProp != null ? [idProp.PropertyType] : [];
    }

    private static string GetCSharpTypeName(Type type) => type switch
    {
        _ when type == typeof(int)     => "int",
        _ when type == typeof(long)    => "long",
        _ when type == typeof(short)   => "short",
        _ when type == typeof(byte)    => "byte",
        _ when type == typeof(uint)    => "uint",
        _ when type == typeof(ulong)   => "ulong",
        _ when type == typeof(float)   => "float",
        _ when type == typeof(double)  => "double",
        _ when type == typeof(decimal) => "decimal",
        _ when type == typeof(bool)    => "bool",
        _ when type == typeof(char)    => "char",
        _ when type == typeof(string)  => "string",
        _                              => $"global::{type.FullName?.Replace('+', '.')}",
    };

    private static int FindMatchingCloseParen(string expression, int openParenPos)
    {
        var depth = 0;
        for (var i = openParenPos; i < expression.Length; i++)
        {
            if (expression[i] == '(') depth++;
            else if (expression[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }
}
