namespace EFQueryLens.Core.Scripting.Contracts;

public static class QueryLensGeneratedPayloadContract
{
    public const int Version = 1;
}

public interface IQueryLensCapturedParameter
{
    string Name { get; }

    string ClrType { get; }

    string DbTypeName { get; }

    string? InferredValue { get; }
}

public interface IQueryLensCapturedCommand
{
    string Sql { get; }

    IReadOnlyList<IQueryLensCapturedParameter> Parameters { get; }
}

public interface IQueryLensExecutionPayload
{
    int PayloadContractVersion { get; }

    object? Queryable { get; }

    string? CaptureSkipReason { get; }

    string? CaptureError { get; }

    IReadOnlyList<IQueryLensCapturedCommand> Commands { get; }
}