using System.Globalization;
using System.Data.Common;

namespace EFQueryLens.Core.Scripting;

internal sealed class SqlCaptureScope : IDisposable
{
    private static readonly AsyncLocal<CaptureState?> Current = new();

    private readonly CaptureState? _previous;
    private readonly CaptureState _state;
    private bool _disposed;

    private SqlCaptureScope(CaptureState? previous, CaptureState state)
    {
        _previous = previous;
        _state = state;
    }

    public IReadOnlyList<CapturedSqlCommand> Commands => _state.Commands;

    public static SqlCaptureScope Begin()
    {
        var previous = Current.Value;
        var state = new CaptureState();
        Current.Value = state;
        return new SqlCaptureScope(previous, state);
    }

    public static void Record(string sql, IEnumerable<DbParameter> parameters)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        var current = Current.Value;
        if (current is null)
        {
            return;
        }

        var capturedParameters = parameters
            .Select(p => new QueryParameter
            {
                Name = string.IsNullOrWhiteSpace(p.ParameterName) ? "@p" : p.ParameterName,
                ClrType = p.DbType.ToString(),
                InferredValue = FormatValue(p.Value),
            })
            .ToList();

        current.Commands.Add(new CapturedSqlCommand(sql, capturedParameters));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Current.Value = _previous;
    }

    private static string? FormatValue(object? value)
    {
        if (value is null || value is DBNull)
        {
            return null;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private sealed class CaptureState
    {
        public List<CapturedSqlCommand> Commands { get; } = [];
    }
}

internal sealed record CapturedSqlCommand(
    string Sql,
    IReadOnlyList<QueryParameter> Parameters);
