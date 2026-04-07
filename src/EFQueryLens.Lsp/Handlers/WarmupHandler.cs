using System.Collections.Concurrent;
using System.Diagnostics;
using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Parsing;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed record WarmupResponse(bool Success, bool Cached, string? AssemblyPath, string? Message);
internal sealed record WarmupPreviewState(bool ShouldDeferPreview, string Reason, string Message, string? AssemblyPath);

internal sealed partial class WarmupHandler
{
    private readonly DocumentManager _documentManager;
    private readonly IQueryLensEngine _engine;
    private bool _debugEnabled;
    private int _successTtlMs;
    private int _failureCooldownMs;
    private readonly ConcurrentDictionary<string, CachedWarmup> _warmCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<WarmupResponse>>> _inflightWarmups =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedWarmup(long ExpiresAtUtcTicks, bool Success, string Message);

    public WarmupHandler(DocumentManager documentManager, IQueryLensEngine engine)
    {
        _documentManager = documentManager;
        _engine = engine;
        _debugEnabled = LspEnvironment.ReadBool("QUERYLENS_DEBUG", fallback: false);
        _successTtlMs = LspEnvironment.ReadInt(
            "QUERYLENS_WARMUP_SUCCESS_TTL_MS",
            fallback: 60_000,
            min: 0,
            max: 600_000);
        _failureCooldownMs = LspEnvironment.ReadInt(
            "QUERYLENS_WARMUP_FAILURE_COOLDOWN_MS",
            fallback: 5_000,
            min: 0,
            max: 120_000);
    }

    public void ApplyClientConfiguration(LspClientConfiguration configuration)
    {
        if (configuration.DebugEnabled.HasValue)
        {
            _debugEnabled = configuration.DebugEnabled.Value;
        }

        if (configuration.WarmupSuccessTtlMs.HasValue)
        {
            _successTtlMs = configuration.WarmupSuccessTtlMs.Value;
        }

        if (configuration.WarmupFailureCooldownMs.HasValue)
        {
            _failureCooldownMs = configuration.WarmupFailureCooldownMs.Value;
        }
    }

    public async Task<WarmupResponse> HandleAsync(TextDocumentPositionParams request, CancellationToken cancellationToken)
    {
        var filePath = DocumentPathResolver.Resolve(request.TextDocument.Uri);
        var documentUri = request.TextDocument.Uri.ToString();

        var sourceText = await GetSourceTextAsync(documentUri, filePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return new WarmupResponse(false, false, null, "empty-source");
        }

        if (LspSyntaxHelper.FindAllLinqChains(sourceText).Count == 0)
        {
            return new WarmupResponse(false, false, null, "no-linq-chain");
        }

        var targetAssembly = AssemblyResolver.TryGetTargetAssembly(filePath);
        if (string.IsNullOrWhiteSpace(targetAssembly)
            || targetAssembly.StartsWith("DEBUG_FAIL", StringComparison.Ordinal)
            || !File.Exists(targetAssembly))
        {
            return new WarmupResponse(false, false, targetAssembly, "assembly-not-found");
        }

        if (TryGetCachedWarmup(targetAssembly, out var cached))
        {
            LogDebug($"warmup-cache-hit assembly={targetAssembly} success={cached.Success} message={cached.Message}");
            return new WarmupResponse(cached.Success, true, targetAssembly, cached.Message);
        }

        var dbContextTypeName = TryResolveDbContextTypeName(
            sourceText,
            request.Position.Line,
            request.Position.Character);

        var (task, _) = GetOrStartWarmupTask(targetAssembly, dbContextTypeName);
        return await task.WaitAsync(cancellationToken);
    }

    internal WarmupPreviewState EnsureWarmupStartedForPreview(
        string filePath,
        string sourceText,
        int line,
        int character)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return new WarmupPreviewState(false, "empty-source", string.Empty, null);
        }

        if (LspSyntaxHelper.FindAllLinqChains(sourceText).Count == 0)
        {
            return new WarmupPreviewState(false, "no-linq-chain", string.Empty, null);
        }

        var targetAssembly = AssemblyResolver.TryGetTargetAssembly(filePath);
        if (string.IsNullOrWhiteSpace(targetAssembly)
            || targetAssembly.StartsWith("DEBUG_FAIL", StringComparison.Ordinal)
            || !File.Exists(targetAssembly))
        {
            return new WarmupPreviewState(false, "assembly-not-found", string.Empty, targetAssembly);
        }

        if (TryGetCachedWarmup(targetAssembly, out var cached))
        {
            LogDebug($"warmup-preview-cache assembly={targetAssembly} success={cached.Success} message={cached.Message}");
            return new WarmupPreviewState(false, cached.Message, string.Empty, targetAssembly);
        }

        var dbContextTypeName = TryResolveDbContextTypeName(sourceText, line, character);
        var (task, isOwner) = GetOrStartWarmupTask(targetAssembly, dbContextTypeName);
        _ = task;

        return isOwner
            ? new WarmupPreviewState(true, "warmup-started", "EF QueryLens - starting and warming up", targetAssembly)
            : new WarmupPreviewState(true, "warmup-inflight", "EF QueryLens - warming up", targetAssembly);
    }

    private async Task<WarmupResponse> ExecuteWarmupAsync(string targetAssembly, string? dbContextTypeName)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            await _engine.InspectModelAsync(new ModelInspectionRequest
            {
                AssemblyPath = targetAssembly,
                DbContextTypeName = dbContextTypeName,
            }, CancellationToken.None);

            sw.Stop();
            CacheWarmup(targetAssembly, success: true, "ready");
            LogDebug($"warmup-success assembly={targetAssembly} elapsedMs={sw.ElapsedMilliseconds} context={dbContextTypeName ?? "<auto>"}");
            return new WarmupResponse(true, false, targetAssembly, "ready");
        }
        catch (Exception ex)
        {
            sw.Stop();

            // Warmup is best-effort; when multiple DbContexts exist and no explicit
            // context can be inferred, avoid surfacing this as a hard warmup failure.
            if (IsMultipleDbContextAmbiguity(ex))
            {
                CacheWarmup(targetAssembly, success: true, "skipped-multi-dbcontext");
                LogDebug($"warmup-skipped assembly={targetAssembly} elapsedMs={sw.ElapsedMilliseconds} reason=multi-dbcontext context={dbContextTypeName ?? "<auto>"}");
                return new WarmupResponse(true, false, targetAssembly, "skipped-multi-dbcontext");
            }

            CacheWarmup(targetAssembly, success: false, ex.GetType().Name);
            LogDebug($"warmup-failed assembly={targetAssembly} elapsedMs={sw.ElapsedMilliseconds} type={ex.GetType().Name} message={ex.Message} context={dbContextTypeName ?? "<auto>"}");
            return new WarmupResponse(false, false, targetAssembly, ex.GetType().Name);
        }
    }

    private (Task<WarmupResponse> Task, bool IsOwner) GetOrStartWarmupTask(string targetAssembly, string? dbContextTypeName)
    {
        var created = new Lazy<Task<WarmupResponse>>(
            () => ExecuteWarmupAsync(targetAssembly, dbContextTypeName),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var inflight = _inflightWarmups.GetOrAdd(targetAssembly, created);
        var isOwner = ReferenceEquals(inflight, created);

        if (isOwner)
        {
            _ = inflight.Value.ContinueWith(
                completedTask => _inflightWarmups.TryRemove(targetAssembly, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            LogDebug($"warmup-inflight-join assembly={targetAssembly} context={dbContextTypeName ?? "<auto>"}");
        }

        return (inflight.Value, isOwner);
    }

}
