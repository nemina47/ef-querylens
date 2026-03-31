using System.Diagnostics;
using System.Reflection;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;
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
            var contextResolutionWatch = Stopwatch.StartNew();
            var dbContextType = ResolveDbContextTypeWithSiblingRetry(alcCtx, request);
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
            var createdContext = await CreateDbContextInstanceAsync(
                dbContextType,
                alcCtx,
                dbContextPoolProvider,
                poolAssemblyPath,
                ct);
            dbContextLease = createdContext.Lease;
            var dbInstance = createdContext.Instance;
            var creationStrategy = createdContext.Strategy;
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

            // 2c. Pre-validate expression syntax before incurring compilation cost.
            var syntaxErrors = RunnerGenerator.ValidateExpressionSyntax(request.Expression);
            if (syntaxErrors.Count > 0)
            {
                return Failure(
                    "Expression syntax error: " + string.Join("; ", syntaxErrors),
                    sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies);
            }

            // 3. Build compilation assembly set and compute eval-runner cache key.
            var compilationAssemblies = BuildCompilationAssemblySet(alcCtx);
            var asmSetHash = ComputeAssemblySetHash(
                compilationAssemblies);
            var namespaceIndexCacheKey =
                $"{Path.GetFullPath(alcCtx.AssemblyPath)}|{alcCtx.AssemblyTimestamp.Ticks}";
            var evalCacheKey =
                $"{alcCtx.AssemblyPath}|{alcCtx.AssemblyTimestamp.Ticks}|{asmSetHash}|{dbContextType.FullName}|{ComputeRequestHash(request)}";
            var useAsyncRunner = request.UseAsyncRunner;

            SyncRunnerInvoker? syncRunner;
            AsyncRunnerInvoker? asyncRunner;
            TimeSpan? metadataReferenceBuildTime;
            TimeSpan? roslynCompilationTime;
            TimeSpan? evalAssemblyLoadTime;
            string? executedExpression;
            if (_evalRunnerCache.TryGetValue(evalCacheKey, out var cachedRunner))
            {
                // Warm path: skip MetadataRefs, namespace scan, compile, emit, load.
                TouchEvalRunnerCacheEntry(evalCacheKey, cachedRunner);
                syncRunner = cachedRunner.SyncInvoker;
                asyncRunner = cachedRunner.AsyncInvoker;
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

                // 5. Build known namespace/type index for import filtering (cached by context identity).
                var (knownNamespaces, knownTypes) = GetOrBuildNamespaceTypeIndex(
                    namespaceIndexCacheKey,
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
                var lastSrc = string.Empty;
                var roslynCompilationWatch = Stopwatch.StartNew();

                string DumpSrcToTemp()
                {
                    return DumpGeneratedSourceToTemp(lastSrc);
                }

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
                    lastSrc = src;

                    if (ShouldDumpGeneratedSource())
                    {
                        var dumpPath = DumpGeneratedSourceToTemp(src);
                        LogDebug($"generated-source dump={dumpPath}");
                    }

                    compilation = BuildCompilation(src, refs);
                    var errors = compilation.GetDiagnostics()
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .ToList();

                    var hardErrors = errors.Where(e => e.Id is not ("CS0103" or "CS1061" or "CS1929" or "CS7036" or "CS0019" or "CS8122" or "CS0246" or "CS0234" or "CS0400" or "CS1503"
                        // CS0122: type/member inaccessible due to protection level.
                        // Caused by internal types in indirect metadata dependencies
                        // (e.g. Microsoft.Data.SqlClient native interop types like SNIHandle).
                        // These are metadata artifacts — the ALC loads assemblies at IL level
                        // where CLR access rules govern, not C# compiler visibility.
                        or "CS0122")).ToList();
                    if (hardErrors.Count > 0)
                    {
                        var rawHardDetail = string.Join("; ", hardErrors.Take(10).Select(e => $"{e.Id}: {e.GetMessage()}"));
                        return Failure(
                            $"Compilation error: {FormatHardDiagnostics(hardErrors)}",
                            sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies,
                            diagnosticDetail: $"{rawHardDetail} | src={DumpSrcToTemp()}");
                    }

                    if (errors.Count == 0)
                        break;

                    if (maxRetries-- <= 0)
                    {
                        var rawDetail = string.Join("; ", errors.Take(10).Select(e => $"{e.Id}: {e.GetMessage()}"));
                        return Failure(
                            $"Compilation error: {FormatSoftDiagnostics(errors)}",
                            sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies,
                            diagnosticDetail: $"{rawDetail} | src={DumpSrcToTemp()}");
                    }

                    compilationRetryCount++;

                    LogDebug($"compile-retry iteration={compilationRetryCount} errorCount={errors.Count}");
                    foreach (var err in errors.Take(10))
                    {
                        LogDebug($"  diagnostic id={err.Id} msg={err.GetMessage()}");
                    }

                    var changed = ApplyCompileRetryAdjustments(
                        errors,
                        compilation,
                        compilationAssemblies,
                        knownNamespaces,
                        knownTypes,
                        dbContextType,
                        workingRequest,
                        stubs,
                        synthesizedUsingStaticTypes,
                        synthesizedUsingNamespaces,
                        ref workingExpression,
                        ref includeGridifyFallbackExtensions);

                    if (!changed)
                    {
                        // If the only remaining errors are metadata accessibility errors
                        // (CS0122 from internal types in indirect assembly dependencies),
                        // retrying will never help — proceed to emit.
                        LogDebug($"compile-retry iteration={compilationRetryCount} no-changes-made");
                        if (errors.All(e => e.Id == "CS0122"))
                            break;

                        var rawNoChangeDetail = string.Join("; ", errors.Take(10).Select(e => $"{e.Id}: {e.GetMessage()}"));
                        return Failure(
                            $"Compilation error: {FormatSoftDiagnostics(errors)}",
                            sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies,
                            diagnosticDetail: $"{rawNoChangeDetail} | src={DumpSrcToTemp()}");
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
                        var emitErrors = emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                        var rawEmitDetail = string.Join("; ", emitErrors.Take(10).Select(e => $"{e.Id}: {e.GetMessage()}"));
                        return Failure(
                            $"Emit error: {FormatHardDiagnostics(emitErrors)}",
                            sw.Elapsed, dbContextType, alcCtx.LoadedAssemblies,
                            diagnosticDetail: $"{rawEmitDetail} | src={DumpSrcToTemp()}");
                    }

                    ms.Position = 0;
                    evalAssembly = alcCtx.LoadEvalAssembly(ms);
                }
                evalAssemblyLoadWatch.Stop();
                evalAssemblyLoadTime = evalAssemblyLoadWatch.Elapsed;

                var runType = evalAssembly.GetType("__QueryLensRunner__")
                    ?? throw new InvalidOperationException("Could not find __QueryLensRunner__ in eval assembly.");

                syncRunner = useAsyncRunner ? null : CreateSyncRunnerInvoker(runType);
                asyncRunner = useAsyncRunner ? CreateAsyncRunnerInvoker(runType) : null;

                executedExpression = string.Equals(workingExpression, originalExpression, StringComparison.Ordinal)
                    ? null
                    : workingExpression;
                _evalRunnerCache[evalCacheKey] = new EvalRunnerEntry(
                    evalAssembly,
                    syncRunner,
                    asyncRunner,
                    GetUtcNowTicks(),
                    executedExpression);
                TrimCacheByLastAccess(_evalRunnerCache, MaxEvalRunnerCacheEntries, static e => e.LastAccessTicks);
            } // end else (eval runner cache miss)

            // 7. Execute and capture SQL.
            var execution = await ExecuteRunnerAndCaptureAsync(
                useAsyncRunner,
                asyncRunner,
                syncRunner,
                dbInstance,
                ct);
            if (execution.FailureReason is not null)
            {
                return Failure(
                    execution.FailureReason,
                    sw.Elapsed,
                    dbContextType,
                    alcCtx.LoadedAssemblies);
            }

            var commands = execution.Commands;
            var warnings = execution.Warnings;
            TimeSpan? runnerExecutionTime = execution.RunnerExecutionTime;

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

    private async Task<(IReadOnlyList<QuerySqlCommand> Commands, List<QueryWarning> Warnings, string? FailureReason, TimeSpan RunnerExecutionTime)> ExecuteRunnerAndCaptureAsync(
        bool useAsyncRunner,
        AsyncRunnerInvoker? asyncRunner,
        SyncRunnerInvoker? syncRunner,
        object dbInstance,
        CancellationToken ct)
    {
        var runnerExecutionWatch = Stopwatch.StartNew();
        var (queryable, captureSkipReason, captureError, capturedCommands) = useAsyncRunner
            ? await InvokeRunMethodAsync(
                asyncRunner ?? throw new InvalidOperationException("Async runner delegate was not initialized."),
                dbInstance,
                ct)
            : ParseExecutionPayload(
                (syncRunner ?? throw new InvalidOperationException("Sync runner delegate was not initialized."))(dbInstance));
        runnerExecutionWatch.Stop();

        if (capturedCommands.Count == 0)
        {
            return ([], [], captureSkipReason ?? captureError ?? "Offline capture produced no SQL commands.", runnerExecutionWatch.Elapsed);
        }

        var warnings = new List<QueryWarning>();
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

        return (capturedCommands, warnings, null, runnerExecutionWatch.Elapsed);
    }

    private bool ApplyCompileRetryAdjustments(
        IReadOnlyList<Diagnostic> errors,
        CSharpCompilation compilation,
        IReadOnlyList<Assembly> compilationAssemblies,
        IReadOnlySet<string> knownNamespaces,
        IReadOnlySet<string> knownTypes,
        Type dbContextType,
        TranslationRequest workingRequest,
        List<string> stubs,
        ISet<string> synthesizedUsingStaticTypes,
        ISet<string> synthesizedUsingNamespaces,
        ref string workingExpression,
        ref bool includeGridifyFallbackExtensions)
    {
        var changed = false;

        var missingNames = errors
            .Where(d => d.Id == "CS0103")
            .Select(TryExtractMissingIdentifierFromDiagnostic)
            .Where(n => n is not null)
            .Distinct()
            .Where(n => !string.IsNullOrWhiteSpace(n)
                        && !LooksLikeTypeOrNamespacePrefix(n, workingRequest.Expression, workingRequest.UsingAliases))
            .ToList();

        LogDebug($"compile-retry cs0103-missing-names={string.Join(",", missingNames)}");

        var rootId = TryExtractRootIdentifier(workingRequest.Expression);
        foreach (var n in missingNames)
        {
            if (stubs.Any(s => s.Contains($" {n} ") || s.Contains($" {n};")))
                continue;

            var stub = BuildStubDeclaration(n!, rootId, workingRequest, dbContextType);
            if (string.IsNullOrWhiteSpace(stub))
                continue;

            LogDebug($"compile-retry stub-added name={n} stub={stub.Trim()}");
            stubs.Add(stub);
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
        var cs0246Types = errors
            .Where(d => d.Id is "CS0246" or "CS0234")
            .Select(d => TryExtractTypeNameFromCS0246(d))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        LogDebug($"compile-retry cs0246-types={string.Join(",", cs0246Types)}");

        foreach (var typeName in cs0246Types)
        {
            var parents = FindNamespacesForSimpleName(typeName!, knownTypes).ToList();
            LogDebug($"compile-retry type-lookup name={typeName} found-parents={string.Join(",", parents)}");

            if (parents.Count == 0)
            {
                LogDebug($"compile-retry type-not-in-known-types name={typeName}");
            }

            foreach (var parent in parents)
            {
                if (IsResolvableNamespace(parent, knownNamespaces))
                {
                    if (synthesizedUsingNamespaces.Add(parent))
                    {
                        LogDebug($"compile-retry using-namespace added={parent}");
                        changed = true;
                    }
                }
                else if (IsResolvableType(parent, knownTypes))
                {
                    // Nested type — bring it into scope with "using static EnclosingType"
                    if (synthesizedUsingStaticTypes.Add(parent))
                    {
                        LogDebug($"compile-retry using-static added={parent}");
                        changed = true;
                    }
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

            var typedStub = BuildStubFromTypeName(expectedType, argName, dbContextType, workingRequest.UsingAliases);
            if (string.IsNullOrWhiteSpace(typedStub))
            {
                stubs.Remove(oldStub);
                changed = true;
                continue;
            }

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

        if (TryNormalizeInaccessibleProjectionTypeFromErrors(
                errors,
                workingExpression,
                out var inaccessibleProjectionNormalizedExpression)
            && !string.Equals(inaccessibleProjectionNormalizedExpression, workingExpression, StringComparison.Ordinal))
        {
            workingExpression = inaccessibleProjectionNormalizedExpression;
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

        return changed;
    }

    private static Type ResolveDbContextTypeWithSiblingRetry(
        ProjectAssemblyContext alcCtx,
        TranslationRequest request)
    {
        try
        {
            return alcCtx.FindDbContextType(
                request.DbContextTypeName,
                request.Expression,
                request.DbContextResolution);
        }
        catch (InvalidOperationException ex) when (IsNoDbContextFoundError(ex))
        {
            TryLoadSiblingAssemblies(alcCtx);
            return alcCtx.FindDbContextType(
                request.DbContextTypeName,
                request.Expression,
                request.DbContextResolution);
        }
    }

    private async Task<(object Instance, string Strategy, IDbContextLease? Lease)> CreateDbContextInstanceAsync(
        Type dbContextType,
        ProjectAssemblyContext alcCtx,
        IDbContextPoolProvider? dbContextPoolProvider,
        string? poolAssemblyPath,
        CancellationToken ct)
    {
        if (dbContextPoolProvider is not null && !string.IsNullOrWhiteSpace(poolAssemblyPath))
        {
            var lease = await dbContextPoolProvider.AcquireDbContextLeaseAsync(
                dbContextType,
                poolAssemblyPath,
                alcCtx.LoadedAssemblies,
                ct);
            return (lease.Instance, lease.Strategy, lease);
        }

        var created = CreateDbContextInstance(
            dbContextType,
            alcCtx.LoadedAssemblies,
            alcCtx.AssemblyPath);
        return (created.Instance, created.Strategy, null);
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

    private static bool TryNormalizeInaccessibleProjectionTypeFromErrors(
        IReadOnlyCollection<Diagnostic> errors,
        string expression,
        out string normalizedExpression)
    {
        normalizedExpression = expression;

        // Private/internal projection DTOs (for example Program.BlogDto) are not visible
        // to the generated eval assembly. Rewrite terminal Select new Type(...) to
        // Select new { ... } so SQL translation can proceed.
        var hasProtectionLevelError = errors.Any(d =>
            d.Id == "CS0122" &&
            d.GetMessage().Contains("inaccessible due to its protection level", StringComparison.OrdinalIgnoreCase));
        if (!hasProtectionLevelError)
            return false;

        ExpressionSyntax parsed;
        try
        {
            parsed = SyntaxFactory.ParseExpression(expression);
        }
        catch
        {
            return false;
        }

        var rewriter = new InaccessibleProjectionRewriter();
        var rewritten = (ExpressionSyntax?)rewriter.Visit(parsed);
        if (!rewriter.Changed || rewritten is null)
            return false;

        normalizedExpression = rewritten.ToString();
        return !string.Equals(normalizedExpression, expression, StringComparison.Ordinal);
    }

    private sealed class InaccessibleProjectionRewriter : CSharpSyntaxRewriter
    {
        public bool Changed { get; private set; }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node);
            if (visited?.Expression is not MemberAccessExpressionSyntax member
                || member.Name.Identifier.ValueText != "Select"
                || visited.ArgumentList.Arguments.Count != 1)
                return visited;

            var argument = visited.ArgumentList.Arguments[0];
            var rewrittenArgExpression = RewriteProjectionLambda(visited, argument.Expression);
            if (rewrittenArgExpression is null)
                return visited;

            Changed = true;
            var rewrittenArg = argument.WithExpression(rewrittenArgExpression);
            return visited.WithArgumentList(
                visited.ArgumentList.WithArguments(
                    SyntaxFactory.SingletonSeparatedList(rewrittenArg)));
        }

        private static ExpressionSyntax? RewriteProjectionLambda(
            InvocationExpressionSyntax selectInvocation,
            ExpressionSyntax expr)
        {
            switch (expr)
            {
                case SimpleLambdaExpressionSyntax simple when simple.Body is ObjectCreationExpressionSyntax objectCreation:
                {
                    var expectedNames = CollectDownstreamMemberNames(selectInvocation, simple.Parameter.Identifier.ValueText);
                    return simple.WithBody(BuildAnonymousProjection(objectCreation, expectedNames));
                }
                case ParenthesizedLambdaExpressionSyntax paren when paren.Body is ObjectCreationExpressionSyntax objectCreation:
                {
                    var lambdaParam = paren.ParameterList.Parameters.FirstOrDefault()?.Identifier.ValueText;
                    var expectedNames = string.IsNullOrWhiteSpace(lambdaParam)
                        ? []
                        : CollectDownstreamMemberNames(selectInvocation, lambdaParam!);
                    return paren.WithBody(BuildAnonymousProjection(objectCreation, expectedNames));
                }
                default:
                    return null;
            }
        }

        private static AnonymousObjectCreationExpressionSyntax BuildAnonymousProjection(
            ObjectCreationExpressionSyntax objectCreation,
            IReadOnlyList<string> expectedNames)
        {
            var args = objectCreation.ArgumentList?.Arguments ?? [];
            var members = new List<AnonymousObjectMemberDeclaratorSyntax>();

            for (var i = 0; i < args.Count; i++)
            {
                var expression = args[i].Expression.WithoutTrivia();
                var expectedName = i < expectedNames.Count ? expectedNames[i] : null;
                var memberName = !string.IsNullOrWhiteSpace(expectedName)
                    ? expectedName!
                    : TryInferMemberName(expression) ?? $"__ql{i}";
                members.Add(
                    SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.NameEquals(memberName),
                        expression));
            }

            if (members.Count == 0)
            {
                members.Add(
                    SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.NameEquals("__ql0"),
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
            }

            return SyntaxFactory.AnonymousObjectCreationExpression(
                SyntaxFactory.SeparatedList(members));
        }

        private static IReadOnlyList<string> CollectDownstreamMemberNames(
            InvocationExpressionSyntax selectInvocation,
            string lambdaParameter)
        {
            // Inspect only the immediately chained invocation (if any), which covers the
            // common pattern: .Select(x => new PrivateDto(...)).Select(x => new { x.A, x.B })
            // and allows us to preserve A/B member names when rewriting the first projection.
            if (selectInvocation.Parent is not MemberAccessExpressionSyntax parentAccess
                || parentAccess.Parent is not InvocationExpressionSyntax chainedInvocation)
            {
                return [];
            }

            return chainedInvocation.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(m => m.Expression is IdentifierNameSyntax id
                            && string.Equals(id.Identifier.ValueText, lambdaParameter, StringComparison.Ordinal))
                .Select(m => m.Name.Identifier.ValueText)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static string? TryInferMemberName(ExpressionSyntax expression) =>
            expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                _ => null,
            };
    }
}
