using System.Reflection;

namespace QueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
    private static (object? Queryable, string? CaptureSkipReason, string? CaptureError, IReadOnlyList<QuerySqlCommand> Commands)
        ParseExecutionPayload(object? payload)
    {
        if (payload is null)
            return (null, "Generated runner returned null payload.", null, []);

        var payloadType = payload.GetType();
        var queryable = payloadType.GetProperty("Queryable", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(payload);
        var captureSkipReason = payloadType.GetProperty("CaptureSkipReason", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(payload) as string;
        var captureError = payloadType.GetProperty("CaptureError", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(payload) as string;

        var commandsObj = payloadType.GetProperty("Commands", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(payload) as System.Collections.IEnumerable;

        var commands = new List<QuerySqlCommand>();
        if (commandsObj is null)
            return (queryable, captureSkipReason, captureError, commands);

        foreach (var cmdObj in commandsObj)
        {
            if (cmdObj is null)
                continue;

            var cmdType = cmdObj.GetType();
            var sql = cmdType.GetProperty("Sql", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(cmdObj) as string;

            if (string.IsNullOrWhiteSpace(sql))
                continue;

            var parameters = new List<QueryParameter>();
            var paramsObj = cmdType.GetProperty("Parameters", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(cmdObj) as System.Collections.IEnumerable;

            if (paramsObj is not null)
            {
                foreach (var paramObj in paramsObj)
                {
                    if (paramObj is null)
                        continue;

                    var paramType = paramObj.GetType();
                    var name = paramType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(paramObj) as string;
                    var clrType = paramType.GetProperty("ClrType", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(paramObj) as string;
                    var inferredValue = paramType.GetProperty("InferredValue", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(paramObj) as string;

                    parameters.Add(new QueryParameter
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? "@p" : name,
                        ClrType = string.IsNullOrWhiteSpace(clrType) ? "object" : clrType,
                        InferredValue = inferredValue,
                    });
                }
            }

            commands.Add(new QuerySqlCommand
            {
                Sql = sql,
                Parameters = parameters,
            });
        }

        return (queryable, captureSkipReason, captureError, commands);
    }

    private static bool IsQueryable(object? value) =>
        value?.GetType().GetInterfaces()
            .Any(i => i.FullName == "System.Linq.IQueryable") == true;

    /// <summary>
    /// Last-resort fallback - calls <c>ToQueryString()</c> via reflection on the user's
    /// EF Core assembly. Used only when execution-based capture fails.
    /// </summary>
    private static string? TryToQueryString(object? queryable, IEnumerable<Assembly> userAssemblies)
    {
        if (queryable is null)
            return null;

        foreach (var asm in userAssemblies.Where(a => a.GetName().Name == "Microsoft.EntityFrameworkCore"))
        {
            var ext = asm.GetType("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions");
            var method = ext?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ToQueryString" && m.GetParameters().Length == 1 && !m.IsGenericMethod);
            if (method is null)
                continue;

            try
            {
                return (string?)method.Invoke(null, [queryable]);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
