using System.Diagnostics;
using System.Reflection;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Core.Scripting.Evaluation;

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
        var originalExpression = request.Expression;

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

            // 2b. If the expression contains Find/FindAsync, rewrite key args to default(PKType)
            // using the EF model — avoids the ArgumentException on PK type mismatch.
            if (ContainsFindInvocation(request.Expression))
            {
                var rewritten = TryRewriteFindExpression(request.Expression, dbInstance);
                if (rewritten != null)
                    request = request with { Expression = rewritten };
                else
                    return Failure(
                        "DbSet.Find() and FindAsync() require the entity's primary key type from " +
                        "the EF model, but the model lookup failed for this DbSet. " +
                        "Use db.YourEntities.Where(e => e.Id == id).FirstOrDefault() instead.",
                        sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
            }

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
            string? executedExpression;
            if (_evalRunnerCache.TryGetValue(evalCacheKey, out var cachedRunner))
            {
                // Warm path: skip MetadataRefs, namespace scan, compile, emit, load.
                TouchEvalRunnerCacheEntry(evalCacheKey, cachedRunner);
                runMethod = cachedRunner.RunMethod;
                executedExpression = cachedRunner.ExecutedExpression;
                metadataReferenceBuildTime = TimeSpan.Zero;
                roslynCompilationTime = TimeSpan.Zero;
                evalAssemblyLoadTime = TimeSpan.Zero;
            }
            else
            {
                // 4. Retrieve or build MetadataReferences for this assembly set.
                var metadataReferenceWatch = Stopwatch.StartNew();
                var refs = GetOrBuildMetadataRefs(alcCtx, compilationAssemblies, asmSetHash);
                metadataReferenceWatch.Stop();
                metadataReferenceBuildTime = metadataReferenceWatch.Elapsed;

                // 5. Build known namespace/type index for import filtering (cached by assemblySetHash).
                var (knownNamespaces, knownTypes) = GetOrBuildNamespaceTypeIndex(
                    asmSetHash,
                    compilationAssemblies);

                // 6. Compile -> emit -> load into user ALC -> invoke Run.
                // Retry with auto-stub declarations on CS0103 (missing local variables).
                var workingExpression = request.Expression;
                var stubs = new List<string>();
                var synthesizedUsingStaticTypes = new HashSet<string>(StringComparer.Ordinal);
                var synthesizedUsingNamespaces = new HashSet<string>(StringComparer.Ordinal);
                var includeGridifyFallbackExtensions = false;
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
                        synthesizedUsingStaticTypes,
                        synthesizedUsingNamespaces,
                        includeGridifyFallbackExtensions);
                    compilation = BuildCompilation(src, refs);
                    var errors = compilation.GetDiagnostics()
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .ToList();

                    var hardErrors = errors.Where(e => e.Id is not ("CS0103" or "CS1061" or "CS1929" or "CS7036" or "CS0019" or "CS8122" or "CS0246" or "CS0234" or "CS0400" or "CS1503")).ToList();
                    if (hardErrors.Count > 0)
                    {
                        return Failure(
                            $"Compilation error: {FormatHardDiagnostics(hardErrors)}",
                            sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
                    }

                    if (errors.Count == 0)
                        break;

                    if (maxRetries-- <= 0)
                    {
                        return Failure(
                            $"Compilation error: {FormatSoftDiagnostics(errors)}",
                            sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
                    }

                    compilationRetryCount++;

                    var missingNames = errors
                        .Where(d => d.Id == "CS0103")
                        .Select(TryExtractMissingIdentifierFromDiagnostic)
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

                    // CS0246 / CS0234: type or namespace not found. The type likely lives in a
                    // namespace that the source file doesn't need to import explicitly (e.g. a DTO
                    // in the same namespace as the calling class). Locate it in the loaded
                    // assemblies and synthesise a using directive automatically.
                    // Use the diagnostic message text (same regex as FormatSoftDiagnostics) to
                    // extract the simple type name — more reliable than AST span lookup.
                    //
                    // Two cases:
                    //  • Top-level type   → parent is a namespace → emit "using Ns;"
                    //  • Nested type      → parent is a class     → emit "using static EnclosingType;"
                    foreach (var typeName in errors
                        .Where(d => d.Id is "CS0246" or "CS0234")
                        .Select(d => TryExtractTypeNameFromCS0246(d))
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.Ordinal))
                    {
                        foreach (var parent in FindNamespacesForSimpleName(typeName!, knownTypes))
                        {
                            if (IsResolvableNamespace(parent, knownNamespaces))
                            {
                                if (synthesizedUsingNamespaces.Add(parent))
                                    changed = true;
                            }
                            else if (IsResolvableType(parent, knownTypes))
                            {
                                // Nested type — bring it into scope with "using static EnclosingType"
                                if (synthesizedUsingStaticTypes.Add(parent))
                                    changed = true;
                            }
                        }
                    }

                    // CS1503: argument type mismatch — a stub was generated as 'object' because
                    // no type could be inferred (e.g. a pattern string passed to EF.Functions.Like).
                    // Extract the expected type from the diagnostic message and re-generate the
                    // stub with the correct type so overload resolution succeeds on retry.
                    foreach (var cs1503 in errors.Where(e => e.Id == "CS1503"))
                    {
                        var argName = TryExtractSimpleIdentifierAtDiagnosticLocation(cs1503);
                        if (argName is null)
                            continue;

                        var expectedType = TryExtractExpectedTypeFromCS1503(cs1503);
                        if (string.IsNullOrWhiteSpace(expectedType))
                            continue;

                        // Only retypestub if we actually have a stub for this identifier.
                        var oldStub = stubs.FirstOrDefault(s =>
                            s.Contains($" {argName} ", StringComparison.Ordinal)
                            || s.Contains($" {argName};", StringComparison.Ordinal));
                        if (oldStub is null)
                            continue;

                        var typedStub = BuildStubFromTypeName(expectedType, argName);
                        if (string.Equals(oldStub, typedStub, StringComparison.Ordinal))
                            continue;

                        stubs.Remove(oldStub);
                        stubs.Add(typedStub);
                        changed = true;
                    }

                    if (TryNormalizeRootContextHopFromErrors(
                            errors,
                            compilation,
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

                    foreach (var import in InferMissingExtensionStaticImports(errors, compilation, compilationAssemblies))
                    {
                        if (synthesizedUsingStaticTypes.Add(import))
                        {
                            changed = true;
                        }
                    }

                        if (TryApplyGridifyFallbackFromErrors(
                            errors,
                            stubs,
                            ref includeGridifyFallbackExtensions))
                    {
                        changed = true;
                    }

                    if (!changed)
                    {
                        return Failure(
                            $"Compilation error: {FormatSoftDiagnostics(errors)}",
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
                            $"Emit error: {FormatHardDiagnostics(emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))}",
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

                executedExpression = string.Equals(workingExpression, originalExpression, StringComparison.Ordinal)
                    ? null
                    : workingExpression;
                _evalRunnerCache[evalCacheKey] = new EvalRunnerEntry(evalAssembly, runMethod, GetUtcNowTicks(), executedExpression);
                TrimCacheByLastAccess(_evalRunnerCache, MaxEvalRunnerCacheEntries, static e => e.LastAccessTicks);
            } // end else (eval runner cache miss)

            // 7. Execute and capture SQL.
            var warnings = new List<QueryWarning>();
            IReadOnlyList<QuerySqlCommand> commands;

            var runnerExecutionWatch = Stopwatch.StartNew();
            var runPayload = runMethod.Invoke(null, [dbInstance]);
            var (queryable, captureSkipReason, captureError, capturedCommands) = ParseExecutionPayload(runPayload);
            runnerExecutionWatch.Stop();
            TimeSpan? runnerExecutionTime = runnerExecutionWatch.Elapsed;

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
                return Failure(
                    captureSkipReason ?? captureError ?? "Offline capture produced no SQL commands.",
                    sw.Elapsed,
                    dbContextType,
                    alcCtx.LoadedAssemblies);
            }

            sw.Stop();
            var stageTimings = new EvaluationStageTimings(
                contextResolutionTime,
                dbContextCreationTime,
                metadataReferenceBuildTime,
                roslynCompilationTime,
                compilationRetryCount,
                evalAssemblyLoadTime,
                runnerExecutionTime);

            return new QueryTranslationResult
            {
                Success = true,
                Sql = commands[0].Sql,
                Commands = commands,
                Parameters = commands[0].Parameters,
                Warnings = warnings,
                Metadata = BuildMetadata(dbContextType, alcCtx.LoadedAssemblies, sw.Elapsed, creationStrategy, stageTimings),
                ExecutedExpression = executedExpression,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var unwrapped = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            var msg = unwrapped is TargetInvocationException ? unwrapped.ToString() : unwrapped.Message;

            if (msg.Contains("does not have a type mapping assigned", StringComparison.OrdinalIgnoreCase))
            {
                msg += "\n\nHint: A variable in your query has a type that EF Core cannot map to a SQL parameter type. " +
                       "This often happens with provider-specific value types (e.g. Pgvector.Vector for pgvector, " +
                       "NetTopologySuite.Geometries.Point for spatial). Ensure the variable is typed explicitly in " +
                       "the hovered expression, or assign it from a typed entity property.";
            }
            else if (unwrapped is MissingMethodException ||
                     msg.Contains("Method not found", StringComparison.OrdinalIgnoreCase))
            {
                msg += "\n\nHint: A method expected by one EF Core assembly was not found in another. " +
                       "This is usually an intra-project version conflict — for example, the EF Core base package " +
                       "and a provider package (SQL Server, Pomelo, Npgsql) resolved to different major or minor " +
                       "versions in your project output. " +
                       "Check that all Microsoft.EntityFrameworkCore.* and provider package references in your " +
                       "project target the same version, and that no transitive dependency is pulling in a " +
                       "mismatched version.";
            }

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

    private static string? TryExtractMissingIdentifierFromDiagnostic(Diagnostic diagnostic)
    {
        if (!diagnostic.Location.IsInSource || diagnostic.Location.SourceTree is null)
            return null;

        var root = diagnostic.Location.SourceTree.GetRoot();
        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

        var identifier = node as IdentifierNameSyntax
            ?? node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault()
            ?? node.AncestorsAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault();

        if (identifier is not null)
            return identifier.Identifier.ValueText;

        var token = root.FindToken(diagnostic.Location.SourceSpan.Start);
        return token.IsKind(SyntaxKind.IdentifierToken)
            ? token.ValueText
            : null;
    }

    /// <summary>
    /// Returns the identifier name at the diagnostic location only when the AST node
    /// is a bare <see cref="IdentifierNameSyntax"/> (i.e. a simple variable reference,
    /// not a member access or method invocation). Used for CS1503 re-stub logic: a
    /// compound expression like <c>someObj.Pattern</c> at the error site would require
    /// changing the stub for <c>someObj</c>, which could be wrong if it is used with a
    /// different type elsewhere in the expression.
    /// </summary>
    private static string? TryExtractSimpleIdentifierAtDiagnosticLocation(Diagnostic diagnostic)
    {
        if (!diagnostic.Location.IsInSource || diagnostic.Location.SourceTree is null)
            return null;

        var root = diagnostic.Location.SourceTree.GetRoot();
        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

        return node is IdentifierNameSyntax identifier
            ? identifier.Identifier.ValueText
            : null;
    }
}
