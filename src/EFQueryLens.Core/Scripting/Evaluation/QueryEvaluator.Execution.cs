using System.Reflection;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Contracts;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    private static async Task<(object? Queryable, string? CaptureSkipReason, string? CaptureError, IReadOnlyList<QuerySqlCommand> Commands)>
        InvokeRunMethodAsync(
            AsyncRunnerInvoker runAsync,
            object dbInstance,
            CancellationToken ct)
    {
        var payload = await runAsync(dbInstance, ct).ConfigureAwait(false);
        return ParseExecutionPayload(payload);
    }

    private static SyncRunnerInvoker CreateSyncRunnerInvoker(Type runType)
    {
        var runMethod = runType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not find Run method in __QueryLensRunner__.");

        try
        {
            return (SyncRunnerInvoker)Delegate.CreateDelegate(typeof(SyncRunnerInvoker), runMethod);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not bind sync runner delegate for __QueryLensRunner__.Run.", ex);
        }
    }

    private static AsyncRunnerInvoker CreateAsyncRunnerInvoker(Type runType)
    {
        var runMethod = runType.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not find RunAsync method in __QueryLensRunner__.");

        try
        {
            return (AsyncRunnerInvoker)Delegate.CreateDelegate(typeof(AsyncRunnerInvoker), runMethod);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not bind async runner delegate for __QueryLensRunner__.RunAsync.", ex);
        }
    }

    private static (object? Queryable, string? CaptureSkipReason, string? CaptureError, IReadOnlyList<QuerySqlCommand> Commands)
        ParseExecutionPayload(object? payload)
    {
        if (payload is null)
            return (null, "Generated runner returned null payload.", null, []);

        if (payload is not IQueryLensExecutionPayload typedPayload)
        {
            throw new InvalidOperationException(
                $"Payload contract mismatch: generated runner returned '{payload.GetType().FullName}', " +
                $"which does not implement {nameof(IQueryLensExecutionPayload)}.");
        }

        if (typedPayload.PayloadContractVersion != QueryLensGeneratedPayloadContract.Version)
        {
            throw new InvalidOperationException(
                $"Payload contract version mismatch: expected {QueryLensGeneratedPayloadContract.Version} " +
                $"but runner returned {typedPayload.PayloadContractVersion}.");
        }

        var commands = typedPayload.Commands
            .Where(static command => !string.IsNullOrWhiteSpace(command.Sql))
            .Select(static command => new QuerySqlCommand
            {
                Sql = command.Sql,
                Parameters = command.Parameters
                    .Where(static parameter => parameter is not null)
                    .Select(static parameter => new QueryParameter
                    {
                        Name = string.IsNullOrWhiteSpace(parameter.Name) ? "@p" : parameter.Name,
                        ClrType = string.IsNullOrWhiteSpace(parameter.ClrType)
                            ? (string.IsNullOrWhiteSpace(parameter.DbTypeName) ? "object" : parameter.DbTypeName)
                            : parameter.ClrType,
                        InferredValue = parameter.InferredValue,
                    })
                    .ToList(),
            })
            .ToList();

        return (
            typedPayload.Queryable,
            typedPayload.CaptureSkipReason,
            typedPayload.CaptureError,
            commands);
    }
}

