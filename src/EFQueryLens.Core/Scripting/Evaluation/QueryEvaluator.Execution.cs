using System.Collections.Concurrent;
using System.Reflection;
using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    private sealed record PayloadAccessors(
        PropertyInfo Queryable,
        PropertyInfo CaptureSkipReason,
        PropertyInfo CaptureError,
        PropertyInfo Commands);

    private sealed record CommandAccessors(PropertyInfo? Sql, PropertyInfo? Parameters);

    private sealed record ParameterAccessors(PropertyInfo? Name, PropertyInfo? ClrType, PropertyInfo? InferredValue);

    private static readonly ConcurrentDictionary<Type, PayloadAccessors> SPayloadAccessors = new();
    private static readonly ConcurrentDictionary<Type, CommandAccessors> SCommandAccessors = new();
    private static readonly ConcurrentDictionary<Type, ParameterAccessors> SParameterAccessors = new();

    private static (object? Queryable, string? CaptureSkipReason, string? CaptureError, IReadOnlyList<QuerySqlCommand> Commands)
        ParseExecutionPayload(object? payload)
    {
        if (payload is null)
            return (null, "Generated runner returned null payload.", null, []);

        var payloadType = payload.GetType();

        // Validate structure upfront to fail fast on runner/parser version mismatches.
        var payloadAccessors = ValidatePayloadStructure(payloadType);
        var queryable = payloadAccessors.Queryable.GetValue(payload);
        var captureSkipReason = payloadAccessors.CaptureSkipReason.GetValue(payload) as string;
        var captureError = payloadAccessors.CaptureError.GetValue(payload) as string;

        var commandsObj = payloadAccessors.Commands.GetValue(payload) as System.Collections.IEnumerable;

        var commands = new List<QuerySqlCommand>();
        if (commandsObj is null)
            return (queryable, captureSkipReason, captureError, commands);

        foreach (var cmdObj in commandsObj)
        {
            if (cmdObj is null)
                continue;

            var cmdType = cmdObj.GetType();
            var commandAccessors = SCommandAccessors.GetOrAdd(cmdType, static t =>
            {
                var sql = t.GetProperty("Sql", BindingFlags.Public | BindingFlags.Instance);
                var parameters = t.GetProperty("Parameters", BindingFlags.Public | BindingFlags.Instance);
                return new CommandAccessors(sql, parameters);
            });

            var sql = commandAccessors.Sql?.GetValue(cmdObj) as string;

            if (string.IsNullOrWhiteSpace(sql))
                continue;

            var parameters = new List<QueryParameter>();
            var paramsObj = commandAccessors.Parameters?.GetValue(cmdObj) as System.Collections.IEnumerable;

            if (paramsObj is not null)
            {
                foreach (var paramObj in paramsObj)
                {
                    if (paramObj is null)
                        continue;

                    var paramType = paramObj.GetType();
                    var parameterAccessors = SParameterAccessors.GetOrAdd(paramType, static t =>
                    {
                        var name = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        var clrType = t.GetProperty("ClrType", BindingFlags.Public | BindingFlags.Instance);
                        var inferredValue = t.GetProperty("InferredValue", BindingFlags.Public | BindingFlags.Instance);
                        return new ParameterAccessors(name, clrType, inferredValue);
                    });

                    var name = parameterAccessors.Name?.GetValue(paramObj) as string;
                    var clrType = parameterAccessors.ClrType?.GetValue(paramObj) as string;
                    var inferredValue = parameterAccessors.InferredValue?.GetValue(paramObj) as string;

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

    /// <summary>
    /// Validates that the payload has the expected structure.
    /// Throws InvalidOperationException if structure mismatch is detected.
    /// </summary>
    private static PayloadAccessors ValidatePayloadStructure(Type payloadType)
    {
        return SPayloadAccessors.GetOrAdd(payloadType, static t =>
        {
            var properties = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(p => p.Name, StringComparer.Ordinal);

            var expectedProperties = new[] { "Queryable", "CaptureSkipReason", "CaptureError", "Commands" };
            foreach (var expected in expectedProperties)
            {
                if (!properties.ContainsKey(expected))
                {
                    throw new InvalidOperationException(
                        $"Payload structure mismatch: missing property '{expected}'. " +
                        $"Actual: {string.Join(", ", properties.Keys.OrderBy(static x => x, StringComparer.Ordinal))}. " +
                        "This may indicate a version conflict between the script runner and parser.");
                }
            }

            var commandsProp = properties["Commands"];
            if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(commandsProp.PropertyType))
            {
                throw new InvalidOperationException(
                    $"Payload structure mismatch: property 'Commands' must be enumerable but was '{commandsProp.PropertyType.FullName}'.");
            }

            return new PayloadAccessors(
                properties["Queryable"],
                properties["CaptureSkipReason"],
                properties["CaptureError"],
                commandsProp);
        });
    }
}

