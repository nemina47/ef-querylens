using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using EFQueryLens.Core.AssemblyContext;

namespace EFQueryLens.Core.Scripting;

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
/// before execution. The connection's command stubs intercept every
/// <c>DbCommand.Execute*</c> call, record the SQL + parameters into
/// <see cref="SqlCaptureScope"/> (an <c>AsyncLocal</c>-based collector), and return a
/// <see cref="FakeDbDataReader"/> so EF Core materializes "rows" without error.
/// </para>
///
/// No real database connection is ever opened.
/// </summary>
public sealed partial class QueryEvaluator
{
    // Building MetadataReference objects from disk is expensive (100-500 ms for a
    // large project). Cache them keyed on assembly path + last-write timestamp +
    // assembly-set hash so the cost is paid only on initial load or after a rebuild.
    private sealed record MetadataRefEntry(
        DateTime AssemblyTimestamp,
        string AssemblySetHash,
        MetadataReference[] Refs);

    private readonly ConcurrentDictionary<string, MetadataRefEntry> _refCache = new();

    // Roslyn compilation options are reused across all eval compilations.
    private static readonly CSharpCompilationOptions s_compilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: false,
            nullableContextOptions: NullableContextOptions.Disable);

    private static readonly CSharpParseOptions s_parseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

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

            // 6. Compile -> emit -> load into user ALC -> invoke Run.
            // Retry with auto-stub declarations on CS0103 (missing local variables).
            var workingExpression = request.Expression;
            var stubs = new List<string>();
            var synthesizedUsingStaticTypes = new HashSet<string>(StringComparer.Ordinal);
            var maxRetries = 5;
            CSharpCompilation compilation = null!;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var workingRequest = request with { Expression = workingExpression };

                var src = BuildEvalSource(
                    dbContextType,
                    workingRequest,
                    stubs,
                    knownNamespaces,
                    knownTypes,
                    synthesizedUsingStaticTypes);
                compilation = BuildCompilation(src, refs);
                var errors = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                var hardErrors = errors.Where(e => e.Id is not ("CS0103" or "CS1061" or "CS0019" or "CS8122")).ToList();
                if (hardErrors.Count > 0)
                {
                    return Failure(
                        $"Compilation error: {string.Join("; ", hardErrors.Select(d => d.GetMessage()))}",
                        sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
                }

                if (errors.Count == 0)
                    break;

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
                                && !LooksLikeTypeOrNamespacePrefix(n!, workingRequest.Expression, workingRequest.UsingAliases))
                    .ToList();

                var changed = false;

                var rootId = TryExtractRootIdentifier(workingRequest.Expression);
                foreach (var n in missingNames)
                {
                    if (stubs.Any(s => s.Contains($" {n} ") || s.Contains($" {n};")))
                        continue;

                    stubs.Add(BuildStubDeclaration(n!, rootId, workingRequest, dbContextType));
                    changed = true;
                }

                if (TryNormalizeRootContextHopFromErrors(
                        errors,
                        workingRequest.Expression,
                        dbContextType,
                        out var normalizedExpression)
                    && !string.Equals(normalizedExpression, workingExpression, StringComparison.Ordinal))
                {
                    workingExpression = normalizedExpression;
                    changed = true;
                }

                if (TryNormalizePatternTernaryComparisonFromErrors(
                        errors,
                        workingExpression,
                        out var ternaryNormalizedExpression)
                    && !string.Equals(ternaryNormalizedExpression, workingExpression, StringComparison.Ordinal))
                {
                    workingExpression = ternaryNormalizedExpression;
                    changed = true;
                }

                if (TryNormalizeUnsupportedPatternMatchingFromErrors(
                        errors,
                        workingExpression,
                        out var patternNormalizedExpression)
                    && !string.Equals(patternNormalizedExpression, workingExpression, StringComparison.Ordinal))
                {
                    workingExpression = patternNormalizedExpression;
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

            var runPayload = runMethod.Invoke(null, [dbInstance]);
            var (queryable, captureSkipReason, captureError, capturedCommands) = ParseExecutionPayload(runPayload);

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
                {
                    return Failure(
                        $"Expression did not return an IQueryable. Got: '{queryable?.GetType().Name ?? "null"}'.",
                        sw.Elapsed,
                        dbContextType,
                        alcCtx.LoadedAssemblies);
                }

                var sql = TryToQueryString(queryable, alcCtx.LoadedAssemblies);
                if (sql is null)
                {
                    return Failure(
                        captureSkipReason ?? captureError ?? "Could not generate SQL.",
                        sw.Elapsed,
                        dbContextType,
                        alcCtx.LoadedAssemblies);
                }

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
                    sw.Elapsed,
                    dbContextType,
                    alcCtx.LoadedAssemblies);
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
                Success = true,
                Sql = commands[0].Sql,
                Commands = commands,
                Parameters = commands[0].Parameters,
                Warnings = warnings,
                Metadata = BuildMetadata(dbContextType, alcCtx.LoadedAssemblies, sw.Elapsed, creationStrategy),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = ex is TargetInvocationException { InnerException: { } inner }
                ? inner.ToString()
                : ex.Message;
            return Failure(msg, sw.Elapsed, null, null);
        }
    }

    private static (object Instance, string Strategy) CreateDbContextInstance(
        Type dbContextType,
        IEnumerable<Assembly> userAssemblies)
    {
        var all = AssemblyLoadContext.Default.Assemblies.Concat(userAssemblies);

        var fromQueryLens = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            dbContextType,
            all,
            out var queryLensFailure);
        if (fromQueryLens is not null)
            return (fromQueryLens, "querylens-factory");

        var fromDesignTime = DesignTimeDbContextFactory.TryCreate(
            dbContextType,
            all,
            out var designTimeFailure);
        if (fromDesignTime is not null)
            return (fromDesignTime, "design-time-factory");

        var details = string.Join(" ", new[] { queryLensFailure, designTimeFailure }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        throw new InvalidOperationException(
            $"No factory found for '{dbContextType.FullName}'. " +
            "Add an IQueryLensDbContextFactory<T> implementation to your project. " +
            "See the QueryLens README for setup instructions." +
            (string.IsNullOrWhiteSpace(details) ? string.Empty : $" Details: {details}"));
    }
}
