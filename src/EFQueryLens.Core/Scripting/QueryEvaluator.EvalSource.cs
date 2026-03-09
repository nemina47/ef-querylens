using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EFQueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
    private static string BuildEvalSource(
        Type dbContextType,
        TranslationRequest request,
        IReadOnlyList<string> stubs,
        IReadOnlySet<string> knownNamespaces,
        IReadOnlySet<string> knownTypes,
        IReadOnlyCollection<string> synthesizedUsingStaticTypes)
    {
        var sb = new StringBuilder();

        sb.AppendLine("using System;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Collections;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Data;");
        sb.AppendLine("using System.Data.Common;");
        sb.AppendLine("using System.Globalization;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.EntityFrameworkCore;");

        foreach (var import in request.AdditionalImports)
        {
            if (IsValidUsingName(import) && IsResolvableNamespace(import, knownNamespaces))
                sb.AppendLine($"using {import};");
        }

        foreach (var kvp in request.UsingAliases
                     .Where(kvp => IsValidAliasName(kvp.Key)
                                   && IsValidUsingName(kvp.Value)
                                   && IsResolvableTypeOrNamespace(kvp.Value, knownNamespaces, knownTypes))
                     .OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"using {kvp.Key} = {kvp.Value};");
        }

        foreach (var st in request.UsingStaticTypes
                     .Where(st => IsValidUsingName(st) && IsResolvableType(st, knownTypes))
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            sb.AppendLine($"using static {st};");
        }

        foreach (var st in synthesizedUsingStaticTypes
                     .Where(IsValidUsingName)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            sb.AppendLine($"using static {st};");
        }

        sb.AppendLine();
        sb.AppendLine("public sealed class __QueryLensCapturedParameter__");
        sb.AppendLine("{");
        sb.AppendLine("    public string Name { get; set; } = \"@p\";");
        sb.AppendLine("    public string ClrType { get; set; } = \"object\";");
        sb.AppendLine("    public string? InferredValue { get; set; }");
        sb.AppendLine("}");

        sb.AppendLine();
        sb.AppendLine("public sealed class __QueryLensCapturedSqlCommand__");
        sb.AppendLine("{");
        sb.AppendLine("    public string Sql { get; set; } = string.Empty;");
        sb.AppendLine("    public __QueryLensCapturedParameter__[] Parameters { get; set; } = Array.Empty<__QueryLensCapturedParameter__>();");
        sb.AppendLine("}");

        sb.AppendLine();
        sb.AppendLine("public sealed class __QueryLensExecutionResult__");
        sb.AppendLine("{");
        sb.AppendLine("    public object? Queryable { get; set; }");
        sb.AppendLine("    public string? CaptureSkipReason { get; set; }");
        sb.AppendLine("    public string? CaptureError { get; set; }");
        sb.AppendLine("    public __QueryLensCapturedSqlCommand__[] Commands { get; set; } = Array.Empty<__QueryLensCapturedSqlCommand__>();");
        sb.AppendLine("}");

        sb.AppendLine();
        sb.AppendLine("internal sealed class __QueryLensOfflineDbConnection__ : DbConnection");
        sb.AppendLine("{");
        sb.AppendLine("    public override string ConnectionString { get; set; } = string.Empty;");
        sb.AppendLine("    public override string Database => \"offline\";");
        sb.AppendLine("    public override string DataSource => \"localhost\";");
        sb.AppendLine("    public override string ServerVersion => \"0\";");
        sb.AppendLine("    public override ConnectionState State => ConnectionState.Open;");
        sb.AppendLine("    public override void ChangeDatabase(string databaseName) { }");
        sb.AppendLine("    public override void Open() { }");
        sb.AppendLine("    public override void Close() { }");
        sb.AppendLine("    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new InvalidOperationException(\"Offline mode: transactions not supported.\");");
        sb.AppendLine("    protected override DbCommand CreateDbCommand() => new __QueryLensOfflineDbCommand__();");

        sb.AppendLine("    private sealed class __QueryLensOfflineDbCommand__ : DbCommand");
        sb.AppendLine("    {");
        sb.AppendLine("        private readonly __QueryLensOfflineDbParameterCollection__ _parameters = new();");
        sb.AppendLine("        public override string CommandText { get; set; } = string.Empty;");
        sb.AppendLine("        public override int CommandTimeout { get; set; }");
        sb.AppendLine("        public override CommandType CommandType { get; set; }");
        sb.AppendLine("        public override bool DesignTimeVisible { get; set; }");
        sb.AppendLine("        public override UpdateRowSource UpdatedRowSource { get; set; }");
        sb.AppendLine("        protected override DbConnection? DbConnection { get; set; }");
        sb.AppendLine("        protected override DbParameterCollection DbParameterCollection => _parameters;");
        sb.AppendLine("        protected override DbTransaction? DbTransaction { get; set; }");
        sb.AppendLine("        public override void Cancel() { }");
        sb.AppendLine("        public override void Prepare() { }");
        sb.AppendLine("        protected override DbParameter CreateDbParameter() => new __QueryLensOfflineDbParameter__();");
        sb.AppendLine("        public override int ExecuteNonQuery() { RecordCurrentCommand(); return 0; }");
        sb.AppendLine("        public override object? ExecuteScalar() { RecordCurrentCommand(); return null; }");
        sb.AppendLine("        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { RecordCurrentCommand(); return new __QueryLensFakeDbDataReader__(); }");
        sb.AppendLine("        private void RecordCurrentCommand() => __QueryLensSqlCaptureScope__.Record(CommandText, _parameters.Items);");
        sb.AppendLine("    }");

        sb.AppendLine("    private sealed class __QueryLensOfflineDbParameterCollection__ : DbParameterCollection");
        sb.AppendLine("    {");
        sb.AppendLine("        private readonly List<DbParameter> _items = new();");
        sb.AppendLine("        public IReadOnlyList<DbParameter> Items => _items;");
        sb.AppendLine("        public override int Count => _items.Count;");
        sb.AppendLine("        public override object SyncRoot => ((ICollection)_items).SyncRoot!;");
        sb.AppendLine("        public override bool IsFixedSize => false;");
        sb.AppendLine("        public override bool IsReadOnly => false;");
        sb.AppendLine("        public override bool IsSynchronized => false;");
        sb.AppendLine("        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }");
        sb.AppendLine("        public override void AddRange(Array values) { foreach (var v in values) Add(v!); }");
        sb.AppendLine("        public override void Clear() => _items.Clear();");
        sb.AppendLine("        public override bool Contains(object value) => value is DbParameter p && _items.Contains(p);");
        sb.AppendLine("        public override bool Contains(string value) => IndexOf(value) >= 0;");
        sb.AppendLine("        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);");
        sb.AppendLine("        public override IEnumerator GetEnumerator() => _items.GetEnumerator();");
        sb.AppendLine("        public override int IndexOf(object value) => value is DbParameter p ? _items.IndexOf(p) : -1;");
        sb.AppendLine("        public override int IndexOf(string parameterName) => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));");
        sb.AppendLine("        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);");
        sb.AppendLine("        public override void Remove(object value) { if (value is DbParameter p) _items.Remove(p); }");
        sb.AppendLine("        public override void RemoveAt(int index) => _items.RemoveAt(index);");
        sb.AppendLine("        public override void RemoveAt(string parameterName) { var i = IndexOf(parameterName); if (i >= 0) _items.RemoveAt(i); }");
        sb.AppendLine("        protected override DbParameter GetParameter(int index) => _items[index];");
        sb.AppendLine("        protected override DbParameter GetParameter(string parameterName) { var i = IndexOf(parameterName); return i >= 0 ? _items[i] : throw new IndexOutOfRangeException(parameterName); }");
        sb.AppendLine("        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;");
        sb.AppendLine("        protected override void SetParameter(string parameterName, DbParameter value) { var i = IndexOf(parameterName); if (i >= 0) _items[i] = value; else _items.Add(value); }");
        sb.AppendLine("    }");

        sb.AppendLine("    private sealed class __QueryLensOfflineDbParameter__ : DbParameter");
        sb.AppendLine("    {");
        sb.AppendLine("        public override DbType DbType { get; set; }");
        sb.AppendLine("        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;");
        sb.AppendLine("        public override bool IsNullable { get; set; }");
        sb.AppendLine("        public override string ParameterName { get; set; } = string.Empty;");
        sb.AppendLine("        public override string SourceColumn { get; set; } = string.Empty;");
        sb.AppendLine("        public override object? Value { get; set; }");
        sb.AppendLine("        public override bool SourceColumnNullMapping { get; set; }");
        sb.AppendLine("        public override int Size { get; set; }");
        sb.AppendLine("        public override void ResetDbType() { }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        sb.AppendLine();
        sb.AppendLine("internal sealed class __QueryLensFakeDbDataReader__ : DbDataReader");
        sb.AppendLine("{");
        sb.AppendLine("    private int _position = -1;");
        // Keep this high so EF buffered materialization doesn't fail for wide entities/projections.
        sb.AppendLine("    public override int FieldCount => 1024;");
        sb.AppendLine("    public override bool HasRows => true;");
        sb.AppendLine("    public override bool IsClosed => false;");
        sb.AppendLine("    public override int RecordsAffected => 0;");
        sb.AppendLine("    public override int Depth => 0;");
        sb.AppendLine("    public override object this[int ordinal] => 0;");
        sb.AppendLine("    public override object this[string name] => 0;");
        sb.AppendLine("    public override bool Read() { _position++; return _position < 1; }");
        sb.AppendLine("    public override bool NextResult() => false;");
        sb.AppendLine("    public override string GetName(int ordinal) => $\"c{ordinal}\";");
        sb.AppendLine("    public override string GetDataTypeName(int ordinal) => \"object\";");
        sb.AppendLine("    public override Type GetFieldType(int ordinal) => typeof(object);");
        sb.AppendLine("    public override object GetValue(int ordinal) => 0;");
        sb.AppendLine("    public override int GetValues(object[] values)");
        sb.AppendLine("    {");
        sb.AppendLine("        var count = Math.Min(values.Length, FieldCount);");
        sb.AppendLine("        for (var i = 0; i < count; i++) values[i] = 0;");
        sb.AppendLine("        return count;");
        sb.AppendLine("    }");
        sb.AppendLine("    public override int GetOrdinal(string name) => 0;");
        sb.AppendLine("    public override bool GetBoolean(int ordinal) => false;");
        sb.AppendLine("    public override byte GetByte(int ordinal) => 0;");
        sb.AppendLine("    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;");
        sb.AppendLine("    public override char GetChar(int ordinal) => '\\0';");
        sb.AppendLine("    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;");
        sb.AppendLine("    public override Guid GetGuid(int ordinal) => Guid.Empty;");
        sb.AppendLine("    public override short GetInt16(int ordinal) => 0;");
        sb.AppendLine("    public override int GetInt32(int ordinal) => 0;");
        sb.AppendLine("    public override long GetInt64(int ordinal) => 0;");
        sb.AppendLine("    public override float GetFloat(int ordinal) => 0;");
        sb.AppendLine("    public override double GetDouble(int ordinal) => 0;");
        sb.AppendLine("    public override string GetString(int ordinal) => string.Empty;");
        sb.AppendLine("    public override decimal GetDecimal(int ordinal) => 0m;");
        sb.AppendLine("    public override DateTime GetDateTime(int ordinal) => DateTime.UnixEpoch;");
        sb.AppendLine("    public override bool IsDBNull(int ordinal) => false;");
        sb.AppendLine("    public override T GetFieldValue<T>(int ordinal)");
        sb.AppendLine("    {");
        sb.AppendLine("        var t = typeof(T);");
        sb.AppendLine("        if (t == typeof(string)) return (T)(object)string.Empty;");
        sb.AppendLine("        if (t == typeof(Guid)) return (T)(object)Guid.Empty;");
        sb.AppendLine("        if (t == typeof(DateTime)) return (T)(object)DateTime.UnixEpoch;");
        sb.AppendLine("        if (t.IsValueType) return (T)Activator.CreateInstance(t)!;");
        sb.AppendLine("        return default!;");
        sb.AppendLine("    }");
        sb.AppendLine("    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => Task.FromResult(GetFieldValue<T>(ordinal));");
        sb.AppendLine("    public override IEnumerator GetEnumerator() { while (Read()) yield return this; }");
        sb.AppendLine("    public override DataTable GetSchemaTable() => new();");
        sb.AppendLine("}");

        sb.AppendLine();
        sb.AppendLine("internal sealed class __QueryLensSqlCaptureScope__ : IDisposable");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly AsyncLocal<List<__QueryLensCapturedSqlCommand__>?> Current = new();");
        sb.AppendLine("    private readonly List<__QueryLensCapturedSqlCommand__>? _previous;");
        sb.AppendLine("    private readonly List<__QueryLensCapturedSqlCommand__> _state;");
        sb.AppendLine("    private bool _disposed;");
        sb.AppendLine("    private __QueryLensSqlCaptureScope__(List<__QueryLensCapturedSqlCommand__>? previous, List<__QueryLensCapturedSqlCommand__> state)");
        sb.AppendLine("    {");
        sb.AppendLine("        _previous = previous;");
        sb.AppendLine("        _state = state;");
        sb.AppendLine("    }");
        sb.AppendLine("    public static __QueryLensSqlCaptureScope__ Begin()");
        sb.AppendLine("    {");
        sb.AppendLine("        var previous = Current.Value;");
        sb.AppendLine("        var state = new List<__QueryLensCapturedSqlCommand__>();");
        sb.AppendLine("        Current.Value = state;");
        sb.AppendLine("        return new __QueryLensSqlCaptureScope__(previous, state);");
        sb.AppendLine("    }");
        sb.AppendLine("    public static void Record(string sql, IEnumerable<DbParameter> parameters)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (string.IsNullOrWhiteSpace(sql)) return;");
        sb.AppendLine("        var current = Current.Value;");
        sb.AppendLine("        if (current is null) return;");
        sb.AppendLine("        var capturedParameters = parameters");
        sb.AppendLine("            .Select(p => new __QueryLensCapturedParameter__");
        sb.AppendLine("            {");
        sb.AppendLine("                Name = string.IsNullOrWhiteSpace(p.ParameterName) ? \"@p\" : p.ParameterName,");
        sb.AppendLine("                ClrType = p.DbType.ToString(),");
        sb.AppendLine("                InferredValue = p.Value is null || p.Value is DBNull ? null : Convert.ToString(p.Value, CultureInfo.InvariantCulture),");
        sb.AppendLine("            })");
        sb.AppendLine("            .ToArray();");
        sb.AppendLine("        current.Add(new __QueryLensCapturedSqlCommand__ { Sql = sql, Parameters = capturedParameters });");
        sb.AppendLine("    }");
        sb.AppendLine("    public __QueryLensCapturedSqlCommand__[] GetCommands() => _state.ToArray();");
        sb.AppendLine("    public void Dispose()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (_disposed) return;");
        sb.AppendLine("        _disposed = true;");
        sb.AppendLine("        Current.Value = _previous;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        sb.AppendLine();
        sb.AppendLine("internal static class __QueryLensOfflineCapture__");
        sb.AppendLine("{");
        sb.AppendLine("    public static bool TryInstall(object dbContext, out string? skipReason)");
        sb.AppendLine("    {");
        sb.AppendLine("        skipReason = null;");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            var databaseProp = dbContext.GetType().GetProperty(\"Database\", BindingFlags.Public | BindingFlags.Instance);");
        sb.AppendLine("            if (databaseProp is null)");
        sb.AppendLine("            {");
        sb.AppendLine("                skipReason = \"Could not locate 'Database' property on DbContext.\";");
        sb.AppendLine("                return false;");
        sb.AppendLine("            }");
        sb.AppendLine("            var database = databaseProp.GetValue(dbContext);");
        sb.AppendLine("            if (database is null)");
        sb.AppendLine("            {");
        sb.AppendLine("                skipReason = \"DbContext.Database returned null.\";");
        sb.AppendLine("                return false;");
        sb.AppendLine("            }");
        sb.AppendLine("            var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(dbContext.GetType().Assembly);");
        sb.AppendLine("            var assemblies = alc is null ? Enumerable.Empty<Assembly>() : alc.Assemblies;");
        sb.AppendLine("            var relAsm = assemblies.FirstOrDefault(a => a.GetName().Name == \"Microsoft.EntityFrameworkCore.Relational\");");
        sb.AppendLine("            if (relAsm is null)");
        sb.AppendLine("            {");
        sb.AppendLine("                skipReason = \"Microsoft.EntityFrameworkCore.Relational not loaded - provider may not be relational.\";");
        sb.AppendLine("                return false;");
        sb.AppendLine("            }");
        sb.AppendLine("            var extType = relAsm.GetType(\"Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions\");");
        sb.AppendLine("            if (extType is null)");
        sb.AppendLine("            {");
        sb.AppendLine("                skipReason = \"RelationalDatabaseFacadeExtensions not found in Relational assembly.\";");
        sb.AppendLine("                return false;");
        sb.AppendLine("            }");
        sb.AppendLine("            var setMethod = extType.GetMethods(BindingFlags.Public | BindingFlags.Static)");
        sb.AppendLine("                .Where(m => m.Name == \"SetDbConnection\")");
        sb.AppendLine("                .OrderByDescending(m => m.GetParameters().Length)");
        sb.AppendLine("                .FirstOrDefault();");
        sb.AppendLine("            if (setMethod is null)");
        sb.AppendLine("            {");
        sb.AppendLine("                skipReason = \"SetDbConnection not found on RelationalDatabaseFacadeExtensions.\";");
        sb.AppendLine("                return false;");
        sb.AppendLine("            }");
        sb.AppendLine("            var offlineConn = new __QueryLensOfflineDbConnection__();");
        sb.AppendLine("            var paramCount = setMethod.GetParameters().Length;");
        sb.AppendLine("            var args = paramCount >= 3 ? new object?[] { database, offlineConn, true } : new object?[] { database, offlineConn };");
        sb.AppendLine("            setMethod.Invoke(null, args);");
        sb.AppendLine("            return true;");
        sb.AppendLine("        }");
        sb.AppendLine("        catch (Exception ex)");
        sb.AppendLine("        {");
        sb.AppendLine("            skipReason = $\"SetDbConnection failed: {(ex is TargetInvocationException tie ? tie.InnerException?.Message : ex.Message)}\";");
        sb.AppendLine("            return false;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        sb.AppendLine();
        sb.AppendLine("public static class __QueryLensRunner__");
        sb.AppendLine("{");
        sb.AppendLine("    public static object? Run(object __ctx__)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var {request.ContextVariableName} = ({dbContextType.FullName!.Replace('+', '.')})(object)__ctx__;");

        foreach (var stub in stubs)
            sb.AppendLine($"        {stub}");

        sb.AppendLine("        string? __captureSkipReason = null;");
        sb.AppendLine("        string? __captureError = null;");
        sb.AppendLine($"        var __captureInstalled = __QueryLensOfflineCapture__.TryInstall({request.ContextVariableName}, out __captureSkipReason);");
        sb.AppendLine($"        var __query = (object?)({request.Expression});");
        sb.AppendLine("        var __captured = Array.Empty<__QueryLensCapturedSqlCommand__>();");
        sb.AppendLine("        if (__captureInstalled)");
        sb.AppendLine("        {");
        sb.AppendLine("            using var __scope = __QueryLensSqlCaptureScope__.Begin();");
        sb.AppendLine("            try { EnumerateQueryable(__query); }");
        sb.AppendLine("            catch (Exception ex) { __captureError = ex.GetType().Name + \": \" + ex.Message; }");
        sb.AppendLine("            __captured = __scope.GetCommands();");
        sb.AppendLine("        }");
        sb.AppendLine("        return new __QueryLensExecutionResult__");
        sb.AppendLine("        {");
        sb.AppendLine("            Queryable = __query,");
        sb.AppendLine("            CaptureSkipReason = __captureSkipReason,");
        sb.AppendLine("            CaptureError = __captureError,");
        sb.AppendLine("            Commands = __captured,");
        sb.AppendLine("        };");
        sb.AppendLine("    }");

        sb.AppendLine();
        sb.AppendLine("    private static void EnumerateQueryable(object? queryable)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (queryable is not IEnumerable enumerable) return;");
        sb.AppendLine("        var enumerator = enumerable.GetEnumerator();");
        sb.AppendLine("        try { var guard = 0; while (guard++ < 32 && enumerator.MoveNext()) { } }");
        sb.AppendLine("        finally { (enumerator as IDisposable)?.Dispose(); }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static CSharpCompilation BuildCompilation(string source, MetadataReference[] refs)
    {
        var tree = CSharpSyntaxTree.ParseText(source, s_parseOptions);
        return CSharpCompilation.Create(
            $"__QueryLensEval_{Guid.NewGuid():N}",
            syntaxTrees: [tree],
            references: refs,
            options: s_compilationOptions);
    }
}

