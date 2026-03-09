#pragma warning disable CS8765 // Nullability of parameter type doesn't match overridden member

using System.Collections;
using System.Data;
using System.Data.Common;

namespace EFQueryLens.Core.Scripting;

/// <summary>
/// A minimal <see cref="DbConnection"/> stub that satisfies provider command creation
/// without ever opening a real network connection.
///
/// Injected into the offline <see cref="Microsoft.EntityFrameworkCore.DbContext"/> via
/// <c>DatabaseFacade.SetDbConnection</c> (called via reflection from
/// <see cref="QueryEvaluator"/> so EFQueryLens.Core has no compile-time EF Core reference).
///
/// When EF Core calls <c>CreateCommand()</c>, it receives an <see cref="OfflineDbCommand"/>
/// whose <c>Execute*</c> methods record the commandText + parameters into
/// <see cref="SqlCaptureScope"/> and return a <see cref="FakeDbDataReader"/>.
/// </summary>
internal sealed class OfflineDbConnection : DbConnection
{
    public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "offline";
    public override string DataSource => "localhost";
    public override string ServerVersion => "8.0.0";
    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) { }
    public override void Open() { }
    public override void Close() { }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => throw new InvalidOperationException("Offline mode: transactions not supported.");

    protected override DbCommand CreateDbCommand() => new OfflineDbCommand();

    // ─── OfflineDbCommand ─────────────────────────────────────────────────────

    private sealed class OfflineDbCommand : DbCommand
    {
        private readonly OfflineDbParameterCollection _parameters = new();

        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new OfflineDbParameter();

        public override int ExecuteNonQuery()
        {
            RecordCurrentCommand();
            return 0;
        }

        public override object? ExecuteScalar()
        {
            RecordCurrentCommand();
            return null;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            RecordCurrentCommand();
            return new FakeDbDataReader();
        }

        private void RecordCurrentCommand()
        {
            SqlCaptureScope.Record(CommandText, _parameters.Items);
        }
    }

    // ─── OfflineDbParameterCollection ────────────────────────────────────────

    private sealed class OfflineDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        public IReadOnlyList<DbParameter> Items => _items;

        public override int Count => _items.Count;
        public override object SyncRoot => ((ICollection)_items).SyncRoot!;
        public override bool IsFixedSize => false;
        public override bool IsReadOnly => false;
        public override bool IsSynchronized => false;

        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => value is DbParameter p && _items.Contains(p);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => value is DbParameter p ? _items.IndexOf(p) : -1;
        public override int IndexOf(string parameterName) =>
            _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) { if (value is DbParameter p) _items.Remove(p); }
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) _items.RemoveAt(i); }
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName)
        {
            var i = IndexOf(parameterName);
            return i >= 0 ? _items[i] : throw new IndexOutOfRangeException(parameterName);
        }
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var i = IndexOf(parameterName);
            if (i >= 0) _items[i] = value; else _items.Add(value);
        }
    }

    // ─── OfflineDbParameter ───────────────────────────────────────────────────

    private sealed class OfflineDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        public override string ParameterName { get; set; } = string.Empty;
        public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }
}
