using System.Reflection;
using System.Runtime.CompilerServices;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting;
using EFQueryLens.Core.Scripting.Contracts;
using RunnerExecutor = EFQueryLens.Core.Scripting.Evaluation.RunnerExecutor;

namespace EFQueryLens.Core.Tests;

public class QueryEvaluatorExecutionPayloadTests
{
    private static readonly MethodInfo s_parseExecutionPayload = typeof(RunnerExecutor)
        .GetMethod("ParseExecutionPayload", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find RunnerExecutor.ParseExecutionPayload via reflection.");

    [Fact]
    public void ParseExecutionPayload_NonContractPayload_ThrowsInvalidOperationException()
    {
        var payload = new PayloadMissingCommands
        {
            Queryable = new object(),
            CaptureSkipReason = null,
            CaptureError = null,
        };

        var ex = Assert.Throws<TargetInvocationException>(() => s_parseExecutionPayload.Invoke(null, [payload]));
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Payload contract mismatch", inner.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseExecutionPayload_ContractVersionMismatch_ThrowsInvalidOperationException()
    {
        var payload = new PayloadWrongVersion
        {
            Queryable = new object(),
            CaptureSkipReason = null,
            CaptureError = null,
            Commands = [],
        };

        var ex = Assert.Throws<TargetInvocationException>(() => s_parseExecutionPayload.Invoke(null, [payload]));
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Payload contract version mismatch", inner.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseExecutionPayload_ValidPayload_ParsesSqlCommands()
    {
        var payload = new PayloadValid
        {
            Queryable = null,
            CaptureSkipReason = null,
            CaptureError = null,
            Commands =
            [
                new CommandPayload
                {
                    Sql = "SELECT 1",
                    Parameters =
                    [
                        new ParameterPayload { Name = "@p0", ClrType = "int", InferredValue = "1" },
                    ],
                },
            ],
        };

        var result = s_parseExecutionPayload.Invoke(null, [payload]);
        Assert.NotNull(result);

        var tuple = Assert.IsAssignableFrom<ITuple>(result);
        var commands = Assert.IsAssignableFrom<IReadOnlyList<QuerySqlCommand>>(tuple[3]);
        Assert.Single(commands);
        Assert.Equal("SELECT 1", commands[0].Sql);
        Assert.Single(commands[0].Parameters);
        Assert.Equal("@p0", commands[0].Parameters[0].Name);
    }

    [Fact]
    public void ParseExecutionPayload_DbTypeNameFallback_UsedWhenClrTypeMissing()
    {
        var payload = new PayloadValid
        {
            Queryable = null,
            CaptureSkipReason = null,
            CaptureError = null,
            Commands =
            [
                new CommandPayload
                {
                    Sql = "SELECT 1",
                    Parameters =
                    [
                        new ParameterPayload { Name = "@p0", ClrType = null, DbTypeName = "Int32", InferredValue = "1" },
                    ],
                },
            ],
        };

        var result = s_parseExecutionPayload.Invoke(null, [payload]);
        Assert.NotNull(result);

        var tuple = Assert.IsAssignableFrom<ITuple>(result);
        var commands = Assert.IsAssignableFrom<IReadOnlyList<QuerySqlCommand>>(tuple[3]);
        Assert.Single(commands);
        Assert.Single(commands[0].Parameters);
        Assert.Equal("Int32", commands[0].Parameters[0].ClrType);
    }

    private sealed record PayloadMissingCommands
    {
        public object? Queryable { get; init; }
        public string? CaptureSkipReason { get; init; }
        public string? CaptureError { get; init; }
    }

    private sealed record PayloadCommandsNotEnumerable
    {
        public object? Queryable { get; init; }
        public string? CaptureSkipReason { get; init; }
        public string? CaptureError { get; init; }
        public int Commands { get; init; }
    }

    private sealed record PayloadValid : IQueryLensExecutionPayload
    {
        public int PayloadContractVersion { get; init; } = QueryLensGeneratedPayloadContract.Version;
        public object? Queryable { get; init; }
        public string? CaptureSkipReason { get; init; }
        public string? CaptureError { get; init; }
        public IReadOnlyList<CommandPayload> Commands { get; init; } = [];

        IReadOnlyList<IQueryLensCapturedCommand> IQueryLensExecutionPayload.Commands => Commands;
    }

    private sealed record PayloadWrongVersion : IQueryLensExecutionPayload
    {
        public int PayloadContractVersion { get; init; } = QueryLensGeneratedPayloadContract.Version + 1;
        public object? Queryable { get; init; }
        public string? CaptureSkipReason { get; init; }
        public string? CaptureError { get; init; }
        public IReadOnlyList<CommandPayload> Commands { get; init; } = [];

        IReadOnlyList<IQueryLensCapturedCommand> IQueryLensExecutionPayload.Commands => Commands;
    }

    private sealed record CommandPayload : IQueryLensCapturedCommand
    {
        public string? Sql { get; init; }
        public IReadOnlyList<ParameterPayload> Parameters { get; init; } = [];

        string IQueryLensCapturedCommand.Sql => Sql ?? string.Empty;
        IReadOnlyList<IQueryLensCapturedParameter> IQueryLensCapturedCommand.Parameters => Parameters;
    }

    private sealed record ParameterPayload : IQueryLensCapturedParameter
    {
        public string? Name { get; init; }
        public string? ClrType { get; init; }
        public string? DbTypeName { get; init; }
        public string? InferredValue { get; init; }

        string IQueryLensCapturedParameter.Name => Name ?? "@p";
        string IQueryLensCapturedParameter.ClrType => ClrType ?? string.Empty;
        string IQueryLensCapturedParameter.DbTypeName => DbTypeName ?? string.Empty;
        string? IQueryLensCapturedParameter.InferredValue => InferredValue;
    }
}
