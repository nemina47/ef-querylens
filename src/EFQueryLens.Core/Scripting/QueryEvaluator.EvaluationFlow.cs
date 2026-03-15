using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using EFQueryLens.Core.AssemblyContext;

namespace EFQueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
    private async Task<QueryTranslationResult> EvaluateAsyncInternal(
        ProjectAssemblyContext alcCtx,
        TranslationRequest request,
        CancellationToken ct,
        IDbContextPoolProvider? dbContextPoolProvider,
        string? poolAssemblyPath)
    {
        var sw = Stopwatch.StartNew();
        var compilationRetryCount = 0;
        IDbContextLease? dbContextLease = null;

        try
        {
            // 1. Resolve the DbContext type from the user's ALC.
            Type dbContextType;
            var contextResolutionWatch = Stopwatch.StartNew();
            try
            {
                dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName, request.Expression);
            }
            catch (InvalidOperationException ex) when (IsNoDbContextFoundError(ex))
            {
                TryLoadSiblingAssemblies(alcCtx);
                dbContextType = alcCtx.FindDbContextType(request.DbContextTypeName, request.Expression);
            }
            contextResolutionWatch.Stop();
            TimeSpan? contextResolutionTime = contextResolutionWatch.Elapsed;

            if (IsUnsupportedTopLevelMethodInvocation(request.Expression, request.ContextVariableName))
            {
                return Failure(
                    "Top-level method invocations (e.g. service.GetXxx(...)) are not supported " +
                    "for SQL preview. Hover a direct IQueryable chain (for example: " +
                    "dbContext.Entities.Where(...)) or hover inside the method where the query is built.",
                    sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
            }

            // 2. Create DbContext via factory (QueryLens-native or EF Design-Time).
            var dbContextCreationWatch = Stopwatch.StartNew();
            object dbInstance;
            string creationStrategy;
            if (dbContextPoolProvider is not null && !string.IsNullOrWhiteSpace(poolAssemblyPath))
            {
                dbContextLease = await dbContextPoolProvider.AcquireDbContextLeaseAsync(
                    dbContextType,
                    poolAssemblyPath,
                    alcCtx.LoadedAssemblies,
                    ct);
                dbInstance = dbContextLease.Instance;
                creationStrategy = dbContextLease.Strategy;
            }
            else
            {
                var created = CreateDbContextInstance(
                    dbContextType,
                    alcCtx.LoadedAssemblies,
                    alcCtx.AssemblyPath);
                dbInstance = created.Instance;
                creationStrategy = created.Strategy;
            }
            dbContextCreationWatch.Stop();
            TimeSpan? dbContextCreationTime = dbContextCreationWatch.Elapsed;

            // 3. Build compilation assembly set and compute eval-runner cache key.
            var compilationAssemblies = BuildCompilationAssemblySet(alcCtx);
            var asmSetHash = ComputeAssemblySetHash(
                compilationAssemblies);
            var evalCacheKey =
                $"{alcCtx.AssemblyPath}|{alcCtx.AssemblyTimestamp.Ticks}|{asmSetHash}|{dbContextType.FullName}|{ComputeRequestHash(request)}";

            MethodInfo runMethod;
            TimeSpan? metadataReferenceBuildTime;
            TimeSpan? roslynCompilationTime;
            TimeSpan? evalAssemblyLoadTime;
            if (_evalRunnerCache.TryGetValue(evalCacheKey, out var cachedRunner))
            {
                // Warm path: skip MetadataRefs, namespace scan, compile, emit, load.
                runMethod = cachedRunner.RunMethod;
                metadataReferenceBuildTime = TimeSpan.Zero;
                roslynCompilationTime = TimeSpan.Zero;
                evalAssemblyLoadTime = TimeSpan.Zero;
            }
            else
            {
                // 4. Retrieve or build MetadataReferences for this assembly set.
                var metadataReferenceWatch = Stopwatch.StartNew();
                var refs = GetOrBuildMetadataRefs(alcCtx, compilationAssemblies);
                metadataReferenceWatch.Stop();
                metadataReferenceBuildTime = metadataReferenceWatch.Elapsed;

                // 5. Build known namespace/type index for import filtering (cached by assemblySetHash).
                var (knownNamespaces, knownTypes) = GetOrBuildNamespaceTypeIndex(asmSetHash, compilationAssemblies);

                // 6. Compile -> emit -> load into user ALC -> invoke Run.
                // Retry with auto-stub declarations on CS0103 (missing local variables).
                var workingExpression = request.Expression;
                var stubs = new List<string>();
                var synthesizedUsingStaticTypes = new HashSet<string>(StringComparer.Ordinal);
                var maxRetries = 5;
                CSharpCompilation compilation;
                var roslynCompilationWatch = Stopwatch.StartNew();

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

                    compilationRetryCount++;

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
                                    && !LooksLikeTypeOrNamespacePrefix(n, workingRequest.Expression, workingRequest.UsingAliases))
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
                roslynCompilationWatch.Stop();
                roslynCompilationTime = roslynCompilationWatch.Elapsed;

                // Emit to MemoryStream and load into the user's isolated ALC.
                var evalAssemblyLoadWatch = Stopwatch.StartNew();
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
                evalAssemblyLoadWatch.Stop();
                evalAssemblyLoadTime = evalAssemblyLoadWatch.Elapsed;

                var runType = evalAssembly.GetType("__QueryLensRunner__")
                    ?? throw new InvalidOperationException("Could not find __QueryLensRunner__ in eval assembly.");
                runMethod = runType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("Could not find Run method in __QueryLensRunner__.");

                _evalRunnerCache[evalCacheKey] = new EvalRunnerEntry(evalAssembly, runMethod);
            } // end else (eval runner cache miss)

            // 7. Execute and capture SQL.
            var warnings = new List<QueryWarning>();
            IReadOnlyList<QuerySqlCommand> commands;

            var runnerExecutionWatch = Stopwatch.StartNew();
            var runPayload = runMethod.Invoke(null, [dbInstance]);
            var (queryable, captureSkipReason, captureError, capturedCommands) = ParseExecutionPayload(runPayload);
            runnerExecutionWatch.Stop();
            TimeSpan? runnerExecutionTime = runnerExecutionWatch.Elapsed;

            TimeSpan? toQueryStringFallbackTime = null;
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

                var toQueryStringStopwatch = Stopwatch.StartNew();
                var sql = TryToQueryString(queryable, alcCtx.LoadedAssemblies);
                toQueryStringStopwatch.Stop();
                toQueryStringFallbackTime = toQueryStringStopwatch.Elapsed;
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
            var stageTimings = new EvaluationStageTimings(
                contextResolutionTime,
                dbContextCreationTime,
                metadataReferenceBuildTime,
                roslynCompilationTime,
                compilationRetryCount,
                evalAssemblyLoadTime,
                runnerExecutionTime,
                toQueryStringFallbackTime);

            return new QueryTranslationResult
            {
                Success = true,
                Sql = commands[0].Sql,
                Commands = commands,
                Parameters = commands[0].Parameters,
                Warnings = warnings,
                Metadata = BuildMetadata(dbContextType, alcCtx.LoadedAssemblies, sw.Elapsed, creationStrategy, stageTimings),
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
        finally
        {
            if (dbContextLease is not null)
            {
                await dbContextLease.DisposeAsync();
            }
        }
    }
}
