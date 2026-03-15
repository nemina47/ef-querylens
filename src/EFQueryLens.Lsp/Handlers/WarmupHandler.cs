using System.Collections.Concurrent;
using System.Diagnostics;
using EFQueryLens.Core;
using EFQueryLens.Lsp.Parsing;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed record WarmupResponse(bool Success, bool Cached, string? AssemblyPath, string? Message);

internal sealed partial class WarmupHandler
{
    private readonly DocumentManager _documentManager;
    private readonly IQueryLensEngine _engine;
    private readonly bool _debugEnabled;
    private readonly int _successTtlMs;
    private readonly int _failureCooldownMs;
    private readonly ConcurrentDictionary<string, CachedWarmup> _warmCache =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedWarmup(long ExpiresAtUtcTicks, bool Success, string Message);

    public WarmupHandler(DocumentManager documentManager, IQueryLensEngine engine)
    {
        _documentManager = documentManager;
        _engine = engine;
        _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);
        _successTtlMs = ReadIntEnvironmentVariable(
            "QUERYLENS_WARMUP_SUCCESS_TTL_MS",
            fallback: 60_000,
            min: 0,
            max: 600_000);
        _failureCooldownMs = ReadIntEnvironmentVariable(
            "QUERYLENS_WARMUP_FAILURE_COOLDOWN_MS",
            fallback: 5_000,
            min: 0,
            max: 120_000);
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

        var sw = Stopwatch.StartNew();
        try
        {
            await _engine.InspectModelAsync(new ModelInspectionRequest
            {
                AssemblyPath = targetAssembly,
                DbContextTypeName = dbContextTypeName,
            }, cancellationToken);

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

}
