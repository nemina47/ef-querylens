using System.Collections;
using System.Data.Common;

namespace EFQueryLens.Core.Scripting;

internal sealed class FakeDbDataReader(int rowCount = 1, int fieldCount = 1024) : DbDataReader
{
    private readonly int _rowCount = Math.Max(0, rowCount);
    private readonly int _fieldCount = Math.Max(1, fieldCount);
    private int _position = -1;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(0);

    public override int Depth => 0;
    public override int FieldCount => _fieldCount;
    public override bool HasRows => _rowCount > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;

    public override bool GetBoolean(int ordinal) => false;
    public override byte GetByte(int ordinal) => 0;
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
    public override char GetChar(int ordinal) => '\0';
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
    public override string GetDataTypeName(int ordinal) => "object";
    public override DateTime GetDateTime(int ordinal) => DateTime.UnixEpoch;
    public override decimal GetDecimal(int ordinal) => 0m;
    public override double GetDouble(int ordinal) => 0d;
    public override Type GetFieldType(int ordinal) => typeof(object);
    public override float GetFloat(int ordinal) => 0f;
    public override Guid GetGuid(int ordinal) => Guid.Empty;
    public override short GetInt16(int ordinal) => 0;
    public override int GetInt32(int ordinal) => 0;
    public override long GetInt64(int ordinal) => 0L;
    public override string GetName(int ordinal) => $"c{ordinal}";
    public override int GetOrdinal(string name) => 0;
    public override string GetString(int ordinal) => string.Empty;
    public override object GetValue(int ordinal) => 0;
    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, _fieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = 0;
        }

        return count;
    }

    public override bool IsDBNull(int ordinal) => false;

    public override bool NextResult() => false;

    public override bool Read()
    {
        _position++;
        return _position < _rowCount;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public override T GetFieldValue<T>(int ordinal)
    {
        var targetType = typeof(T);
        if (targetType == typeof(string))
        {
            return (T)(object)string.Empty;
        }

        if (targetType == typeof(Guid))
        {
            return (T)(object)Guid.Empty;
        }

        if (targetType == typeof(DateTime))
        {
            return (T)(object)DateTime.UnixEpoch;
        }

        if (targetType.IsValueType)
        {
            return (T)Activator.CreateInstance(targetType)!;
        }

        return default!;
    }

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
        Task.FromResult(GetFieldValue<T>(ordinal));

    public override IEnumerator GetEnumerator()
    {
        while (Read())
        {
            yield return this;
        }
    }
}
