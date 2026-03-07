using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using QueryLens.Core.AssemblyContext;

namespace QueryLens.Core.Scripting;

/// <summary>
/// Evaluates a LINQ expression string against an offline <c>DbContext</c> instance
/// loaded via <see cref="ProjectAssemblyContext"/> and returns the captured SQL commands
/// as a <see cref="QueryTranslationResult"/>.
///
/// <para>
/// The expression is compiled by Roslyn (<see cref="CSharpCompilation"/>) into a small
/// in-memory assembly that is loaded into the user's own isolated
/// <see cref="AssemblyLoadContext"/> via <see cref="ProjectAssemblyContext.LoadEvalAssembly"/>.
/// The cast to the concrete DbContext type and all EF Core calls therefore execute in the
/// same ALC as the user's assemblies, so any EF Core major version is supported without
/// cross-version type-identity conflicts.
/// </para>
///
/// <para>
/// SQL is captured by installing an <see cref="OfflineDbConnection"/> on the DbContext
/// before execution.  The connection's command stubs intercept every
/// <c>DbCommand.Execute*</c> call, record the SQL + parameters into
/// <see cref="SqlCaptureScope"/> (an <c>AsyncLocal</c>-based collector), and return a
/// <see cref="FakeDbDataReader"/> so EF Core materialises "rows" without error.
/// </para>
///
/// No real database connection is ever opened.
/// </summary>
public sealed class QueryEvaluator
{
    // ─── MetadataReference cache ──────────────────────────────────────────────
    // Building MetadataReference objects from disk is expensive (100–500 ms for a
    // large project). Cache them keyed on assembly path + last-write timestamp +
    // assembly-set hash so the cost is paid only on initial load or after a rebuild.

    private sealed record MetadataRefEntry(
        DateTime AssemblyTimestamp,
        string AssemblySetHash,
        MetadataReference[] Refs);

    private readonly ConcurrentDictionary<string, MetadataRefEntry> _refCache = new();

    // Roslyn compilation options — constant across all eval compilations.
    private static readonly CSharpCompilationOptions s_compilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: false,
            nullableContextOptions: NullableContextOptions.Disable);

    private static readonly CSharpParseOptions s_parseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Translates a LINQ expression to SQL via execution-based SQL capture.
    /// </summary>
    public async Task<QueryTranslationResult> EvaluateAsync(
        ProjectAssemblyContext alcCtx,
        TranslationRequest request,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // 1. Resolve the DbContext type from the user's ALC.
            Type dbContextType;
            try
            {
                dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName, request.Expression);
            }
            catch (InvalidOperationException ex) when (IsNoDbContextFoundError(ex))
            {
                TryLoadSiblingAssemblies(alcCtx);
                dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName, request.Expression);
            }

            if (IsUnsupportedTopLevelMethodInvocation(request.Expression, request.ContextVariableName))
            {
                return Failure(
                    "Top-level method invocations (e.g. service.GetXxx(...)) are not supported " +
                    "for SQL preview. Hover a direct IQueryable chain (for example: " +
                    "dbContext.Entities.Where(...)) or hover inside the method where the query is built.",
                    sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
            }

            // 2. Create DbContext via factory (QueryLens-native or EF Design-Time).
            var (dbInstance, creationStrategy) =
                CreateDbContextInstance(dbContextType, alcCtx.LoadedAssemblies);

            // 3. Build compilation assembly set for eval runner generation.
            var compilationAssemblies = BuildCompilationAssemblySet(alcCtx);

            // 4. Retrieve or build MetadataReferences for this assembly set.
            var refs = GetOrBuildMetadataRefs(alcCtx, compilationAssemblies);

            // 5. Build known namespace/type index for import filtering.
            var (knownNamespaces, knownTypes) = BuildKnownNamespaceAndTypeIndex(
                compilationAssemblies);

            // 6. Compile → emit → load into user ALC → invoke Run.
            //    Retry with auto-stub declarations on CS0103 (missing local variables).
            var stubs = new List<string>();
            var synthesizedUsingStaticTypes = new HashSet<string>(StringComparer.Ordinal);
            var maxRetries = 5;
            CSharpCompilation compilation = null!;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var src = BuildEvalSource(
                    dbContextType,
                    request,
                    stubs,
                    knownNamespaces,
                    knownTypes,
                    synthesizedUsingStaticTypes);
                compilation = BuildCompilation(src, refs);
                var errors = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                var hardErrors = errors.Where(e => e.Id is not ("CS0103" or "CS1061")).ToList();
                if (hardErrors.Count > 0)
                {
                    return Failure(
                        $"Compilation error: {string.Join("; ", hardErrors.Select(d => d.GetMessage()))}",
                        sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
                }

                if (errors.Count == 0)
                    break; // clean compile — proceed to emit

                if (maxRetries-- <= 0)
                {
                    return Failure(
                        $"Compilation error (too many retries): {string.Join("; ", errors.Select(d => d.GetMessage()))}",
                        sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
                }

                var missingNames = errors
                    .Where(d => d.Id == "CS0103")
                    .Select(d =>
                    {
                        var msg = d.GetMessage();
                        var s = msg.IndexOf('\'');
                        var e2 = msg.IndexOf('\'', s + 1);
                        return s >= 0 && e2 > s ? msg[(s + 1)..e2] : null;
                    })
                    .Where(n => n is not null)
                    .Distinct()
                    .Where(n => !string.IsNullOrWhiteSpace(n)
                                && !LooksLikeTypeOrNamespacePrefix(n!, request.Expression, request.UsingAliases))
                    .ToList();

                var changed = false;

                var rootId = TryExtractRootIdentifier(request.Expression);
                foreach (var n in missingNames)
                {
                    if (stubs.Any(s => s.Contains($" {n} ") || s.Contains($" {n};")))
                        continue;
                    stubs.Add(BuildStubDeclaration(n!, rootId, request, dbContextType));
                    changed = true;
                }

                foreach (var import in InferMissingExtensionStaticImports(errors, compilationAssemblies))
                {
                    if (synthesizedUsingStaticTypes.Add(import))
                    {
                        changed = true;
                    }
                }

                if (!changed)
                {
                    return Failure(
                        $"Compilation error: {string.Join("; ", errors.Select(d => d.GetMessage()))}",
                        sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
                }
            }

            // Emit to MemoryStream and load into the user's isolated ALC.
            Assembly evalAssembly;
            using (var ms = new MemoryStream())
            {
                var emitResult = compilation.Emit(ms);
                if (!emitResult.Success)
                {
                    return Failure(
                        $"Emit error: {string.Join("; ", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage()))}",
                        sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
                }
                ms.Position = 0;
                evalAssembly = alcCtx.LoadEvalAssembly(ms);
            }

            var runType = evalAssembly.GetType("__QueryLensRunner__")
                ?? throw new InvalidOperationException("Could not find __QueryLensRunner__ in eval assembly.");
            var runMethod = runType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Could not find Run method in __QueryLensRunner__.");

            // 7. Execute and capture SQL.
            var warnings = new List<QueryWarning>();
            IReadOnlyList<QuerySqlCommand> commands;
            object? queryable;

            var runPayload = runMethod.Invoke(null, [dbInstance]);
            var (payloadQueryable, captureSkipReason, captureError, capturedCommands) = ParseExecutionPayload(runPayload);
            queryable = payloadQueryable;

            if (capturedCommands.Count > 0)
            {
                commands = capturedCommands;

                if (!string.IsNullOrWhiteSpace(captureError))
                {
                    warnings.Add(new QueryWarning
                    {
                        Severity = WarningSeverity.Warning,
                        Code = "QL_CAPTURE_PARTIAL",
                        Message = "Captured SQL commands, but query materialization failed in offline mode.",
                        Suggestion = captureError,
                    });
                }
            }
            else
            {
                if (!IsQueryable(queryable))
                    return Failure(
                        $"Expression did not return an IQueryable. Got: '{queryable?.GetType().Name ?? "null"}'.",
                        sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);

                var sql = TryToQueryString(queryable, alcCtx.LoadedAssemblies);
                if (sql is null)
                    return Failure(captureSkipReason ?? captureError ?? "Could not generate SQL.",
                        sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);

                commands = [new QuerySqlCommand { Sql = sql, Parameters = ParseParameters(sql) }];

                if (!string.IsNullOrWhiteSpace(captureSkipReason))
                {
                    warnings.Add(new QueryWarning
                    {
                        Severity = WarningSeverity.Info,
                        Code = "QL_CAPTURE_SKIPPED",
                        Message = "Could not install offline connection; used ToQueryString() instead.",
                        Suggestion = captureSkipReason,
                    });
                }
                else if (!string.IsNullOrWhiteSpace(captureError))
                {
                    warnings.Add(new QueryWarning
                    {
                        Severity = WarningSeverity.Warning,
                        Code = "QL_CAPTURE_PARTIAL",
                        Message = "Execution capture failed during materialization; used ToQueryString() instead.",
                        Suggestion = captureError,
                    });
                }
                else
                {
                    warnings.Add(new QueryWarning
                    {
                        Severity = WarningSeverity.Warning,
                        Code = "QL_CAPTURE_FALLBACK",
                        Message = "Execution capture produced no SQL; fell back to ToQueryString().",
                    });
                }
            }

            if (!IsQueryable(queryable))
            {
                return Failure(
                    $"Expression did not return an IQueryable. Got: '{queryable?.GetType().Name ?? "null"}'.",
                    sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
            }

            if (ShouldWarnExpressionPartialRisk(request.Expression, commands))
            {
                AddWarningIfMissing(
                    warnings,
                    new QueryWarning
                    {
                        Severity = WarningSeverity.Warning,
                        Code = "QL_EXPRESSION_PARTIAL_RISK",
                        Message = "Expression selector contains nested materialization that may require additional SQL commands.",
                        Suggestion = "SQL preview is best-effort for this projection shape; child collection commands may be omitted offline.",
                    });
            }

            sw.Stop();
            return new QueryTranslationResult
            {
                Success    = true,
                Sql        = commands[0].Sql,
                Commands   = commands,
                Parameters = commands[0].Parameters,
                Warnings   = warnings,
                Metadata   = BuildMetadata(dbContextType, alcCtx.LoadedAssemblies, sw.Elapsed, creationStrategy),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = ex is TargetInvocationException { InnerException: { } inner }
                ? inner.ToString() : ex.Message;
            return Failure(msg, sw.Elapsed, null, null);
        }
    }

    // ─── DbContext instantiation (factory only) ───────────────────────────────

    private static (object Instance, string Strategy) CreateDbContextInstance(
        Type dbContextType, IEnumerable<Assembly> userAssemblies)
    {
        var all = AssemblyLoadContext.Default.Assemblies.Concat(userAssemblies);

        var fromQueryLens = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            dbContextType, all, out var queryLensFailure);
        if (fromQueryLens is not null) return (fromQueryLens, "querylens-factory");

        var fromDesignTime = DesignTimeDbContextFactory.TryCreate(
            dbContextType, all, out var designTimeFailure);
        if (fromDesignTime is not null) return (fromDesignTime, "design-time-factory");

        var details = string.Join(" ", new[] { queryLensFailure, designTimeFailure }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        throw new InvalidOperationException(
            $"No factory found for '{dbContextType.FullName}'. " +
            "Add an IQueryLensDbContextFactory<T> implementation to your project. " +
            "See the QueryLens README for setup instructions." +
            (string.IsNullOrWhiteSpace(details) ? string.Empty : $" Details: {details}"));
    }

    // ─── MetadataReference cache ──────────────────────────────────────────────

    private MetadataReference[] GetOrBuildMetadataRefs(
        ProjectAssemblyContext alcCtx,
        IReadOnlyList<Assembly> compilationAssemblies)
    {
        var setHash = ComputeAssemblySetHash(compilationAssemblies.ToArray());
        if (_refCache.TryGetValue(alcCtx.AssemblyPath, out var entry)
            && entry.AssemblyTimestamp == alcCtx.AssemblyTimestamp
            && entry.AssemblySetHash == setHash)
            return entry.Refs;

        var refs = CollectMetadataReferences(compilationAssemblies).ToArray();
        _refCache[alcCtx.AssemblyPath] = new MetadataRefEntry(alcCtx.AssemblyTimestamp, setHash, refs);
        return refs;
    }

    private static IEnumerable<MetadataReference> CollectMetadataReferences(IEnumerable<Assembly> assemblies)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var refs = new List<MetadataReference>();

        var expressionsAssembly = assemblies.FirstOrDefault(a =>
            string.Equals(a.GetName().Name, "System.Linq.Expressions", StringComparison.Ordinal));
        var preferredExpressionsMajor = expressionsAssembly?.GetName().Version?.Major;
        var expressionsDir = expressionsAssembly is null
            ? null
            : Path.GetDirectoryName(expressionsAssembly.Location);

        foreach (var asm in assemblies)
        {
            try
            {
                var loc = asm.Location;
                if (string.IsNullOrEmpty(loc) || !seen.Add(loc)) continue;
                var name = asm.GetName().Name;

                // Keep System.Linq.Queryable aligned with the major version of
                // System.Linq.Expressions to avoid mixed net8/net10 reference graphs.
                if (string.Equals(name, "System.Linq.Queryable", StringComparison.Ordinal)
                    && preferredExpressionsMajor.HasValue
                    && asm.GetName().Version?.Major is int qMajor
                    && qMajor != preferredExpressionsMajor.Value)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(name))
                    seenNames.Add(name);
                refs.Add(MetadataReference.CreateFromFile(loc));
            }
            catch { /* dynamic/in-memory assembly — skip */ }
        }

        // If System.Linq.Queryable wasn't loaded yet, try to add it from the same
        // framework directory as System.Linq.Expressions to keep versions aligned.
        if (!seenNames.Contains("System.Linq.Queryable"))
        {
            if (!string.IsNullOrWhiteSpace(expressionsDir))
            {
                var candidate = Path.Combine(expressionsDir, "System.Linq.Queryable.dll");
                if (File.Exists(candidate) && seen.Add(candidate))
                {
                    seenNames.Add("System.Linq.Queryable");
                    refs.Add(MetadataReference.CreateFromFile(candidate));
                }
            }
        }

        return refs;
    }

    // ─── Eval source generation ───────────────────────────────────────────────

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
        sb.AppendLine("    public override int FieldCount => 32;");
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

    // ─── Execution helpers ────────────────────────────────────────────────────

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
    /// Last-resort fallback — calls <c>ToQueryString()</c> via reflection on the user's
    /// EF Core assembly.  Used only when execution-based capture fails.
    /// </summary>
    private static string? TryToQueryString(object? queryable, IEnumerable<Assembly> userAssemblies)
    {
        if (queryable is null) return null;
        foreach (var asm in userAssemblies.Where(a => a.GetName().Name == "Microsoft.EntityFrameworkCore"))
        {
            var ext = asm.GetType("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions");
            var method = ext?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ToQueryString" && m.GetParameters().Length == 1 && !m.IsGenericMethod);
            if (method is null) continue;
            try { return (string?)method.Invoke(null, [queryable]); }
            catch { return null; }
        }
        return null;
    }

    // ─── Parameter parsing (ToQueryString fallback) ───────────────────────────

    private static IReadOnlyList<QueryParameter> ParseParameters(string sql)
    {
        var parameters = new List<QueryParameter>();
        foreach (var line in sql.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("-- @", StringComparison.Ordinal)) continue;
            var content = trimmed[3..].Trim();
            var nameEnd = content.IndexOfAny(['=', ' ']);
            if (nameEnd < 0) continue;
            parameters.Add(new QueryParameter
            {
                Name          = content[..nameEnd].Trim(),
                ClrType       = ExtractDbType(content),
                InferredValue = ExtractInferredValue(content),
            });
        }
        return parameters;
    }

    private static string ExtractDbType(string a)
    {
        const string m = "DbType = ";
        var i = a.IndexOf(m, StringComparison.Ordinal);
        if (i < 0) return "object";
        var s = i + m.Length; var e = a.IndexOf(')', s);
        return e > s ? a[s..e].Trim() : "object";
    }

    private static string? ExtractInferredValue(string a)
    {
        var i = a.IndexOf("='", StringComparison.Ordinal);
        if (i < 0) return null;
        var s = i + 2; var e = a.IndexOf('\'', s);
        return e > s ? a[s..e] : null;
    }

    private static bool ShouldWarnExpressionPartialRisk(
        string expression,
        IReadOnlyList<QuerySqlCommand> commands)
    {
        if (commands.Count != 1 || string.IsNullOrWhiteSpace(expression))
            return false;

        var hasSelect = expression.Contains(".Select(", StringComparison.OrdinalIgnoreCase);
        if (!hasSelect)
            return false;

        var hasNestedMaterialization = expression.Contains(".ToList(", StringComparison.OrdinalIgnoreCase)
            || expression.Contains(".ToArray(", StringComparison.OrdinalIgnoreCase)
            || expression.Contains(".ToDictionary(", StringComparison.OrdinalIgnoreCase)
            || expression.Contains(".ToLookup(", StringComparison.OrdinalIgnoreCase);

        return hasNestedMaterialization;
    }

    private static void AddWarningIfMissing(List<QueryWarning> warnings, QueryWarning warning)
    {
        if (warnings.Any(w => string.Equals(w.Code, warning.Code, StringComparison.OrdinalIgnoreCase)))
            return;

        warnings.Add(warning);
    }

    // ─── Stub generation ──────────────────────────────────────────────────────

    private static string BuildStubDeclaration(
        string name, string? rootId, TranslationRequest request, Type dbContextType)
    {
        if (!string.IsNullOrWhiteSpace(rootId)
            && string.Equals(name, rootId, StringComparison.Ordinal)
            && !string.Equals(name, request.ContextVariableName, StringComparison.Ordinal))
            return $"var {name} = {request.ContextVariableName};";

        var inferred = InferVariableType(name, request.Expression, dbContextType);
        if (inferred is not null)
        {
            var tn = ToCSharpTypeName(inferred);
            return $"{tn} {name} = default({tn});";
        }

        var elem = InferContainsElementType(name, request.Expression, dbContextType);
        if (elem is not null)
        {
            var en = ToCSharpTypeName(elem);
            var containsValues = BuildContainsPlaceholderValues(elem);
            return $"System.Collections.Generic.List<{en}> {name} = new() {{ {containsValues} }};";
        }

        var sel = InferSelectEntityType(name, request.Expression, dbContextType);
        if (sel is not null)
        {
            var sn = ToCSharpTypeName(sel);
            return $"System.Linq.Expressions.Expression<System.Func<{sn}, object>> {name} = _ => default!;";
        }

        if (LooksLikeCancellationTokenArgument(name, request.Expression))
            return $"System.Threading.CancellationToken {name} = default;";

        return $"object {name} = default;";
    }

    // ─── Expression analysis ─────────────────────────────────────────────────

    private static bool LooksLikeTypeOrNamespacePrefix(
        string id, string expression, IReadOnlyDictionary<string, string> aliases)
    {
        if (aliases.ContainsKey(id)) return true;
        if (string.IsNullOrWhiteSpace(id) || !char.IsUpper(id[0])) return false;
        return Regex.IsMatch(expression, $@"(?<!\w){Regex.Escape(id)}\s*\.\s*[A-Z_]");
    }

    private static Type? InferVariableType(string v, string expr, Type ctx)
    {
        var pattern =
            $@"\.(\w+)\s*(?:==|!=|>|<|>=|<=)\s*{Regex.Escape(v)}(?!\w)"
            + "|"
            + $@"(?<!\w){Regex.Escape(v)}\s*(?:==|!=|>|<|>=|<=)\s*\w+\.(\w+)";
        var m = Regex.Match(expr, pattern);
        if (!m.Success) return null;
        return FindEntityPropertyType(ctx, m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    private static Type? InferContainsElementType(string v, string expr, Type ctx)
    {
        var m = Regex.Match(expr, $@"(?<!\w){Regex.Escape(v)}\s*\.\s*Contains\s*\(\s*\w+\s*\.\s*(\w+)");
        return m.Success ? FindEntityPropertyType(ctx, m.Groups[1].Value) : null;
    }

    private static Type? InferSelectEntityType(string v, string expr, Type ctx)
    {
        if (!Regex.IsMatch(expr, $@"\.\s*Select\s*\(\s*{Regex.Escape(v)}\s*\)")) return null;
        var m = Regex.Match(expr, @"^\s*[A-Za-z_][A-Za-z0-9_]*\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)");
        if (!m.Success) return null;
        var prop = ctx.GetProperty(m.Groups[1].Value);
        return prop?.PropertyType.IsGenericType == true
            ? prop.PropertyType.GetGenericArguments().FirstOrDefault() : null;
    }

    private static bool LooksLikeCancellationTokenArgument(string v, string expr)
    {
        if (Regex.IsMatch(expr, $@"\w+Async\s*\([^\)]*\b{Regex.Escape(v)}\b[^\)]*\)")) return true;
        return v.Equals("ct", StringComparison.OrdinalIgnoreCase)
            || v.Equals("cancellationToken", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildContainsPlaceholderValues(Type elementType)
    {
        var t = Nullable.GetUnderlyingType(elementType) ?? elementType;

        if (t == typeof(Guid))
            return "System.Guid.Empty, new System.Guid(\"00000000-0000-0000-0000-000000000001\")";
        if (t == typeof(string))
            return "\"__ql_stub_0\", \"__ql_stub_1\"";
        if (t == typeof(bool))
            return "false, true";
        if (t == typeof(char))
            return "'a', 'b'";
        if (t == typeof(decimal))
            return "0m, 1m";
        if (t == typeof(double))
            return "0d, 1d";
        if (t == typeof(float))
            return "0f, 1f";
        if (t == typeof(long))
            return "0L, 1L";
        if (t == typeof(ulong))
            return "0UL, 1UL";
        if (t == typeof(int))
            return "0, 1";
        if (t == typeof(uint))
            return "0U, 1U";
        if (t == typeof(short))
            return "(short)0, (short)1";
        if (t == typeof(ushort))
            return "(ushort)0, (ushort)1";
        if (t == typeof(byte))
            return "(byte)0, (byte)1";
        if (t == typeof(sbyte))
            return "(sbyte)0, (sbyte)1";
        if (t == typeof(DateTime))
            return "System.DateTime.UnixEpoch, System.DateTime.UnixEpoch.AddDays(1)";
        if (t.IsEnum)
        {
            var enumTypeName = ToCSharpTypeName(t);
            return $"({enumTypeName})0, ({enumTypeName})1";
        }

        var typeName = ToCSharpTypeName(elementType);
        return $"default({typeName})!, default({typeName})!";
    }

    private static IReadOnlyList<string> InferMissingExtensionStaticImports(
        IEnumerable<Diagnostic> errors,
        IEnumerable<Assembly> assemblies)
    {
        var requested = new List<(string ReceiverType, string MethodName)>();
        foreach (var error in errors.Where(e => e.Id == "CS1061"))
        {
            if (!TryParseMissingExtensionDiagnostic(error.GetMessage(), out var receiverType, out var methodName))
                continue;

            if (requested.Any(r => string.Equals(r.ReceiverType, receiverType, StringComparison.Ordinal)
                                   && string.Equals(r.MethodName, methodName, StringComparison.Ordinal)))
                continue;

            requested.Add((receiverType, methodName));
        }

        if (requested.Count == 0)
            return [];

        var imports = new HashSet<string>(StringComparer.Ordinal);

        foreach (var asm in assemblies)
        {
            Type[] allTypes;
            try { allTypes = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle)
            {
                allTypes = rtle.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }
            catch { continue; }

            foreach (var type in allTypes)
            {
                if (!(type.IsAbstract && type.IsSealed))
                    continue;

                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                }
                catch
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    if (!method.IsDefined(typeof(ExtensionAttribute), inherit: false))
                        continue;

                    var request = requested.FirstOrDefault(r => string.Equals(r.MethodName, method.Name, StringComparison.Ordinal));
                    if (request == default)
                        continue;

                    var firstParam = method.GetParameters().FirstOrDefault()?.ParameterType;
                    if (firstParam is null || !IsReceiverNameMatch(firstParam, request.ReceiverType))
                        continue;

                    if (!string.IsNullOrWhiteSpace(type.FullName))
                        imports.Add(type.FullName.Replace('+', '.'));
                }
            }
        }

        return imports.ToArray();
    }

    private static bool TryParseMissingExtensionDiagnostic(
        string message,
        out string receiverType,
        out string methodName)
    {
        receiverType = string.Empty;
        methodName = string.Empty;

        var match = Regex.Match(
            message,
            @"^'(?<receiver>[^']+)' does not contain a definition for '(?<method>[^']+)'.*first argument of type '(?<arg>[^']+)'.*$");

        if (!match.Success)
            return false;

        methodName = match.Groups["method"].Value;
        receiverType = match.Groups["arg"].Success
            ? match.Groups["arg"].Value
            : match.Groups["receiver"].Value;

        return !string.IsNullOrWhiteSpace(receiverType) && !string.IsNullOrWhiteSpace(methodName);
    }

    private static bool IsReceiverNameMatch(Type parameterType, string receiverTypeName)
    {
        if (parameterType.IsByRef)
            parameterType = parameterType.GetElementType() ?? parameterType;

        if (string.Equals(parameterType.Name, receiverTypeName, StringComparison.Ordinal)
            || string.Equals(parameterType.FullName, receiverTypeName, StringComparison.Ordinal))
            return true;

        if (parameterType.IsGenericType)
        {
            var genericName = parameterType.GetGenericTypeDefinition().Name;
            var tick = genericName.IndexOf('`');
            if (tick > 0)
                genericName = genericName[..tick];

            if (string.Equals(genericName, receiverTypeName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? TryExtractRootIdentifier(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        var m = Regex.Match(expression, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:\.|$)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool IsUnsupportedTopLevelMethodInvocation(string expression, string ctxVar)
    {
        var m = Regex.Match(expression,
            @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(");
        if (!m.Success) return false;
        if (string.Equals(m.Groups[1].Value, ctxVar, StringComparison.Ordinal)
            && string.Equals(m.Groups[2].Value, "Set", StringComparison.Ordinal))
            return false;
        return true;
    }

    private static Type? FindEntityPropertyType(Type ctx, string propName)
    {
        foreach (var p in ctx.GetProperties())
        {
            if (!p.PropertyType.IsGenericType) continue;
            var ep = p.PropertyType.GetGenericArguments().FirstOrDefault()?.GetProperty(propName);
            if (ep is not null) return ep.PropertyType;
        }
        return null;
    }

    private static string ToCSharpTypeName(Type t)
    {
        if (t == typeof(void))    return "void";
        if (t == typeof(bool))    return "bool";
        if (t == typeof(byte))    return "byte";
        if (t == typeof(sbyte))   return "sbyte";
        if (t == typeof(char))    return "char";
        if (t == typeof(decimal)) return "decimal";
        if (t == typeof(double))  return "double";
        if (t == typeof(float))   return "float";
        if (t == typeof(int))     return "int";
        if (t == typeof(uint))    return "uint";
        if (t == typeof(long))    return "long";
        if (t == typeof(ulong))   return "ulong";
        if (t == typeof(object))  return "object";
        if (t == typeof(short))   return "short";
        if (t == typeof(ushort))  return "ushort";
        if (t == typeof(string))  return "string";
        if (t.IsArray) return $"{ToCSharpTypeName(t.GetElementType()!)}[]";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            return $"{ToCSharpTypeName(t.GetGenericArguments()[0])}?";
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition().FullName ?? t.Name;
            var tick = def.IndexOf('`'); if (tick >= 0) def = def[..tick];
            return $"{def.Replace('+', '.')}<{string.Join(", ", t.GetGenericArguments().Select(ToCSharpTypeName))}>";
        }
        return (t.FullName ?? t.Name).Replace('+', '.');
    }

    // ─── Import resolvability ─────────────────────────────────────────────────

    private static (HashSet<string> Namespaces, HashSet<string> Types) BuildKnownNamespaceAndTypeIndex(
        IEnumerable<Assembly> assemblies)
    {
        var ns   = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asm in assemblies)
        {
            var key = string.IsNullOrWhiteSpace(asm.Location)
                ? asm.FullName ?? Guid.NewGuid().ToString("N") : asm.Location;
            if (!seen.Add(key)) continue;
            Type[] all;
            try { all = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { all = rtle.Types.Where(t => t is not null).Cast<Type>().ToArray(); }
            catch { continue; }
            foreach (var t in all)
            {
                if (!string.IsNullOrWhiteSpace(t.FullName)) types.Add(t.FullName.Replace('+', '.'));
                if (!string.IsNullOrWhiteSpace(t.Namespace)) AddNamespaceAndParents(t.Namespace, ns);
            }
        }
        return (ns, types);
    }

    private static void AddNamespaceAndParents(string n, ISet<string> dest)
    {
        var span = n.AsSpan();
        while (true)
        {
            dest.Add(span.ToString());
            var dot = span.LastIndexOf('.'); if (dot <= 0) break;
            span = span[..dot];
        }
    }

    private static bool IsResolvableNamespace(string n, IReadOnlySet<string> ns) => ns.Contains(n);
    private static bool IsResolvableType(string n, IReadOnlySet<string> types) => types.Contains(n);
    private static bool IsResolvableTypeOrNamespace(string n, IReadOnlySet<string> ns, IReadOnlySet<string> types) =>
        ns.Contains(n) || types.Contains(n);

    private static bool IsValidAliasName(string a) =>
        !string.IsNullOrWhiteSpace(a) && SyntaxFacts.IsValidIdentifier(a);

    private static bool IsValidUsingName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return !CSharpSyntaxTree.ParseText($"using {name};").GetDiagnostics().Any();
    }

    // ─── Misc helpers ─────────────────────────────────────────────────────────

    private static bool IsNoDbContextFoundError(InvalidOperationException ex) =>
        ex.Message.Contains("No DbContext subclass found", StringComparison.OrdinalIgnoreCase);

    private static void TryLoadSiblingAssemblies(ProjectAssemblyContext alcCtx)
    {
        var dir = Path.GetDirectoryName(alcCtx.AssemblyPath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        var loaded = alcCtx.LoadedAssemblies
            .Select(a => a.Location).Where(l => !string.IsNullOrWhiteSpace(l))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (loaded.Contains(dll)) continue;
            try { alcCtx.LoadAdditionalAssembly(dll); } catch { }
        }
    }

    private static string ComputeAssemblySetHash(Assembly[] assemblies)
    {
        var sb = new StringBuilder();
        foreach (var p in assemblies.Select(a => a.Location)
                     .Where(l => !string.IsNullOrEmpty(l))
                     .Order(StringComparer.OrdinalIgnoreCase))
            sb.Append(p).Append('|');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..8];
    }

    private static IReadOnlyList<Assembly> BuildCompilationAssemblySet(ProjectAssemblyContext alcCtx)
    {
        var userAssemblies = alcCtx.LoadedAssemblies.ToList();
        var userNames = userAssemblies
            .Select(a => a.GetName().Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.Ordinal);

        var merged = new List<Assembly>(userAssemblies);
        foreach (var asm in AssemblyLoadContext.Default.Assemblies)
        {
            var name = asm.GetName().Name;
            if (!string.IsNullOrWhiteSpace(name) && userNames.Contains(name))
                continue;

            merged.Add(asm);
        }

        return merged;
    }

    // ─── Result builders ──────────────────────────────────────────────────────

    private static QueryTranslationResult Failure(
        string message, TimeSpan elapsed, Type? dbContextType, IEnumerable<Assembly>? userAssemblies) =>
        new()
        {
            Success      = false,
            ErrorMessage = message,
            Metadata     = BuildMetadata(dbContextType, userAssemblies, elapsed),
        };

    private static TranslationMetadata BuildMetadata(
        Type? dbContextType,
        IEnumerable<Assembly>? userAssemblies,
        TimeSpan elapsed,
        string creationStrategy = "unknown") =>
        new()
        {
            DbContextType    = dbContextType?.FullName ?? "unknown",
            ProviderName     = userAssemblies is not null ? DetectProviderName(userAssemblies) : "unknown",
            EfCoreVersion    = GetEfCoreVersion(userAssemblies),
            TranslationTime  = elapsed,
            CreationStrategy = creationStrategy,
        };

    private static string DetectProviderName(IEnumerable<Assembly> assemblies)
    {
        foreach (var asm in assemblies)
        {
            var name = asm.GetName().Name;
            if (name is null) continue;
            if (name.StartsWith("Pomelo.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
                return "Pomelo.EntityFrameworkCore.MySql";
            if (name.StartsWith("Npgsql.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
                return "Npgsql.EntityFrameworkCore.PostgreSQL";
            if (name.Equals("Microsoft.EntityFrameworkCore.SqlServer", StringComparison.OrdinalIgnoreCase))
                return "Microsoft.EntityFrameworkCore.SqlServer";
            if (name.Equals("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase))
                return "Microsoft.EntityFrameworkCore.Sqlite";
            if (name.Equals("Microsoft.EntityFrameworkCore.InMemory", StringComparison.OrdinalIgnoreCase))
                return "Microsoft.EntityFrameworkCore.InMemory";
        }
        return "unknown";
    }

    private static string GetEfCoreVersion(IEnumerable<Assembly>? assemblies)
    {
        if (assemblies is null) return "unknown";
        return assemblies.FirstOrDefault(a => a.GetName().Name == "Microsoft.EntityFrameworkCore")
            ?.GetName().Version?.ToString() ?? "unknown";
    }
}

