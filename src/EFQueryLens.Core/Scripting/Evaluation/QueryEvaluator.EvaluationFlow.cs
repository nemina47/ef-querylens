using System.Diagnostics;
using System.Reflection;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;

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
        var originalExpression = request.OriginalExpression ?? request.Expression;
        var rewrittenExpression = request.RewrittenExpression ?? request.Expression;

        try
        {
            // 1. Resolve the DbContext type from the user's ALC.
            var contextResolutionWatch = Stopwatch.StartNew();
            var dbContextType = ResolveDbContextTypeWithSiblingRetry(alcCtx, request);
            contextResolutionWatch.Stop();
            TimeSpan? contextResolutionTime = contextResolutionWatch.Elapsed;

            if (ImportResolver.IsUnsupportedTopLevelMethodInvocation(rewrittenExpression, request.ContextVariableName))
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

            var preCompilationFailure = TryNormalizeAndValidateExpression(
                request with { Expression = rewrittenExpression },
                dbContextType,
                dbInstance,
                sw.Elapsed,
                alcCtx.LoadedAssemblies,
                out request);
            if (preCompilationFailure is not null)
            {
                return preCompilationFailure;
            }

            // 2b. Analyze v2 extraction/capture payloads for deterministic path selection.
            var v2Decision = V2RuntimeAnalyzer.Analyze(request);
            if (!v2Decision.ShouldUseV2Path)
            {
                // V2 path blocked. Return structured diagnostic without fallback.
                var diagnostic = V2RuntimeAnalyzer.FormatDiagnostic(v2Decision);
                return Failure(
                    diagnostic,
                    sw.Elapsed,
                    dbContextType,
                    alcCtx.LoadedAssemblies);
            }

            // 3. Build compilation assembly set and compute eval-runner cache key.
            var compilationAssemblies = BuildCompilationAssemblySet(alcCtx);
            var asmSetHash = CompilationPipeline.ComputeAssemblySetHash(
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
            if (_evalRunnerCache.TryGet(evalCacheKey, out var cachedRunner))
            {
                // Warm path: skip MetadataRefs, namespace scan, compile, emit, load.
                _evalRunnerCache.Touch(evalCacheKey, cachedRunner!);
                syncRunner = cachedRunner.SyncInvoker;
                asyncRunner = cachedRunner.AsyncInvoker;
                executedExpression = cachedRunner.ExecutedExpression;
                metadataReferenceBuildTime = TimeSpan.Zero;
                roslynCompilationTime = TimeSpan.Zero;
                evalAssemblyLoadTime = TimeSpan.Zero;
            }
            else
            {
                var cacheMissFailure = TryBuildRunnerForCacheMiss(
                    alcCtx,
                    request,
                    dbContextType,
                    compilationAssemblies,
                    asmSetHash,
                    namespaceIndexCacheKey,
                    useAsyncRunner,
                    originalExpression,
                    evalCacheKey,
                    sw.Elapsed,
                    ct,
                    ref compilationRetryCount,
                    out syncRunner,
                    out asyncRunner,
                    out executedExpression,
                    out metadataReferenceBuildTime,
                    out roslynCompilationTime,
                    out evalAssemblyLoadTime);
                if (cacheMissFailure is not null)
                {
                    return cacheMissFailure;
                }
            } // end else (eval runner cache miss)

            return await ExecuteAndBuildResultAsync(
                useAsyncRunner,
                asyncRunner,
                syncRunner,
                dbInstance,
                contextResolutionTime,
                dbContextCreationTime,
                metadataReferenceBuildTime,
                roslynCompilationTime,
                compilationRetryCount,
                evalAssemblyLoadTime,
                executedExpression,
                dbContextType,
                alcCtx.LoadedAssemblies,
                creationStrategy,
                sw,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var unwrapped = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return Failure(EnrichRuntimeFailureMessage(unwrapped), sw.Elapsed, null, null);
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
