using System.Diagnostics;
using System.Reflection;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    private QueryTranslationResult? TryNormalizeAndValidateExpression(
        TranslationRequest request,
        Type dbContextType,
        object dbInstance,
        TimeSpan elapsed,
        IEnumerable<Assembly>? loadedAssemblies,
        out TranslationRequest normalizedRequest)
    {
        normalizedRequest = request;

        // If the expression contains Find/FindAsync, rewrite key args to default(PKType)
        // using the EF model - avoids the ArgumentException on PK type mismatch.
        if (ImportResolver.ContainsFindInvocation(request.Expression))
        {
            var rewritten = TryRewriteFindExpression(request.Expression, dbInstance);
            if (rewritten != null)
            {
                normalizedRequest = request with { Expression = rewritten };
            }
            else
            {
                return Failure(
                    "DbSet.Find() and FindAsync() require the entity's primary key type from " +
                    "the EF model, but the model lookup failed for this DbSet. " +
                    "Use db.YourEntities.Where(e => e.Id == id).FirstOrDefault() instead.",
                    elapsed,
                    dbContextType,
                    loadedAssemblies);
            }
        }

        // Pre-validate expression syntax before incurring compilation cost.
        var syntaxErrors = RunnerGenerator.ValidateExpressionSyntax(normalizedRequest.Expression);
        if (syntaxErrors.Count > 0)
        {
            return Failure(
                "Expression syntax error: " + string.Join("; ", syntaxErrors),
                elapsed,
                dbContextType,
                loadedAssemblies);
        }

        return null;
    }

    private async Task<QueryTranslationResult> ExecuteAndBuildResultAsync(
        bool useAsyncRunner,
        AsyncRunnerInvoker? asyncRunner,
        SyncRunnerInvoker? syncRunner,
        object dbInstance,
        TimeSpan? contextResolutionTime,
        TimeSpan? dbContextCreationTime,
        TimeSpan? metadataReferenceBuildTime,
        TimeSpan? roslynCompilationTime,
        int compilationRetryCount,
        TimeSpan? evalAssemblyLoadTime,
        string? executedExpression,
        Type dbContextType,
        IEnumerable<Assembly>? loadedAssemblies,
        string creationStrategy,
        Stopwatch sw,
        CancellationToken ct)
    {
        var execution = await RunnerExecutor.ExecuteRunnerAndCaptureAsync(
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
                loadedAssemblies);
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
            Metadata = BuildMetadata(dbContextType, loadedAssemblies, sw.Elapsed, creationStrategy, stageTimings),
            ExecutedExpression = executedExpression,
        };
    }

    private QueryTranslationResult? TryBuildRunnerForCacheMiss(
        ProjectAssemblyContext alcCtx,
        TranslationRequest request,
        Type dbContextType,
        IReadOnlyList<Assembly> compilationAssemblies,
        string asmSetHash,
        string namespaceIndexCacheKey,
        bool useAsyncRunner,
        string originalExpression,
        string evalCacheKey,
        TimeSpan elapsed,
        CancellationToken ct,
        ref int compilationRetryCount,
        out SyncRunnerInvoker? syncRunner,
        out AsyncRunnerInvoker? asyncRunner,
        out string? executedExpression,
        out TimeSpan? metadataReferenceBuildTime,
        out TimeSpan? roslynCompilationTime,
        out TimeSpan? evalAssemblyLoadTime)
    {
        syncRunner = null;
        asyncRunner = null;
        executedExpression = null;
        metadataReferenceBuildTime = null;
        roslynCompilationTime = null;
        evalAssemblyLoadTime = null;

        var compilationAssemblyList = compilationAssemblies as List<Assembly>
            ?? compilationAssemblies.ToList();

        // Retrieve or build MetadataReferences for this assembly set.
        var metadataReferenceWatch = Stopwatch.StartNew();
        var refs = _compilationPipeline.GetOrBuildMetadataRefs(alcCtx, compilationAssemblyList, asmSetHash);
        metadataReferenceWatch.Stop();
        metadataReferenceBuildTime = metadataReferenceWatch.Elapsed;

        // Build known namespace/type index for import filtering (cached by context identity).
        var (knownNamespaces, knownTypes) = _compilationPipeline.GetOrBuildNamespaceTypeIndex(
            namespaceIndexCacheKey,
            compilationAssemblyList);

        // Compile -> emit -> load into user ALC -> invoke Run.
        // Stubs are synthesized deterministically from the LSP-provided symbol graph.
        var workingExpression = request.Expression;
        LogDebug(
            $"symbol-graph count={request.LocalSymbolGraph.Count} " +
            $"entries={string.Join(",", request.LocalSymbolGraph.Select(s => $"{s.Name}:{s.TypeName}:{s.Kind}:{s.ReplayPolicy}"))}");
        var stubs = StubSynthesizer.BuildInitialStubs(request, dbContextType);
        LogDebug(
            $"stub-list count={stubs.Count} entries={string.Join(" || ", stubs)}");
        var synthesizedUsingStaticTypes = new HashSet<string>(StringComparer.Ordinal);
        var synthesizedUsingNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var roslynCompilationWatch = Stopwatch.StartNew();
        var compileFailure = _compilationPipeline.TryBuildCompilationWithRetries(
            request,
            dbContextType,
            compilationAssemblyList,
            refs,
            knownNamespaces,
            knownTypes,
            stubs,
            synthesizedUsingStaticTypes,
            synthesizedUsingNamespaces,
            elapsed,
            alcCtx.LoadedAssemblies,
            ct,
            ref workingExpression,
            ref compilationRetryCount,
            out var compilation);
        if (compileFailure is not null)
        {
            return compileFailure;
        }

        roslynCompilationWatch.Stop();
        roslynCompilationTime = roslynCompilationWatch.Elapsed;

        // Emit to MemoryStream and load into the user's isolated ALC.
        var evalAssemblyLoadWatch = Stopwatch.StartNew();
        Assembly evalAssembly;
        for (var emitAttempt = 0; ; emitAttempt++)
        {
            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (emitResult.Success)
            {
                ms.Position = 0;
                evalAssembly = alcCtx.LoadEvalAssembly(ms);
                break;
            }

            var emitErrors = emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var normalizedExpression = workingExpression;
            var canRetryInaccessibleProjection =
                emitAttempt == 0
                && emitErrors.Count > 0
                && emitErrors.All(d => d.Id == "CS0122")
                && CompilationPipeline.TryNormalizeInaccessibleProjectionTypeFromErrors(
                    emitErrors,
                    workingExpression,
                    out normalizedExpression)
                && !string.Equals(normalizedExpression, workingExpression, StringComparison.Ordinal);

            if (canRetryInaccessibleProjection)
            {
                workingExpression = normalizedExpression;
                compilationRetryCount++;
                var emitRetryCompileFailure = _compilationPipeline.TryBuildCompilationWithRetries(
                    request,
                    dbContextType,
                    compilationAssemblyList,
                    refs,
                    knownNamespaces,
                    knownTypes,
                    stubs,
                    synthesizedUsingStaticTypes,
                    synthesizedUsingNamespaces,
                    elapsed,
                    alcCtx.LoadedAssemblies,
                    ct,
                    ref workingExpression,
                    ref compilationRetryCount,
                    out compilation);
                if (emitRetryCompileFailure is not null)
                {
                    return emitRetryCompileFailure;
                }

                continue;
            }

            var sourceToDump = compilation.SyntaxTrees.FirstOrDefault()?.ToString() ?? string.Empty;
            var dumpPath = string.IsNullOrWhiteSpace(sourceToDump)
                ? "<empty>"
                : DumpGeneratedSourceToTemp(sourceToDump);
            return FailureFromDiagnostics(
                stage: "Emit error",
                errors: emitErrors,
                elapsed: elapsed,
                dbContextType: dbContextType,
                userAssemblies: alcCtx.LoadedAssemblies,
                softDiagnostics: false,
                sourceDumpPath: dumpPath);
        }

        evalAssemblyLoadWatch.Stop();
        evalAssemblyLoadTime = evalAssemblyLoadWatch.Elapsed;

        var runType = evalAssembly.GetType("__QueryLensRunner__")
            ?? throw new InvalidOperationException("Could not find __QueryLensRunner__ in eval assembly.");

        syncRunner = useAsyncRunner ? null : RunnerExecutor.CreateSyncRunnerInvoker(runType);
        asyncRunner = useAsyncRunner ? RunnerExecutor.CreateAsyncRunnerInvoker(runType) : null;

        executedExpression = string.Equals(workingExpression, originalExpression, StringComparison.Ordinal)
            ? null
            : workingExpression;

        _evalRunnerCache.Store(evalCacheKey, new EvalRunnerCache.Entry(
            evalAssembly,
            syncRunner,
            asyncRunner,
            GetUtcNowTicks(),
            executedExpression));

        return null;
    }

}
