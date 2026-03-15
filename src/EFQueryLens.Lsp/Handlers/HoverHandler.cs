using System.Collections.Concurrent;
using EFQueryLens.Core.Grpc;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    private sealed record MarkedStringOrString(string? First, MarkedString? Second)
    {
        public static implicit operator SumType<string, MarkedString>(MarkedStringOrString value) =>
            value.Second is not null
                ? new SumType<string, MarkedString>(value.Second)
                : new SumType<string, MarkedString>(value.First ?? string.Empty);
    }

    private readonly DocumentManager _documentManager;
    private readonly HoverPreviewService _hoverPreviewService;
    private readonly ConcurrentDictionary<string, CachedHoverResult> _hoverCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedHoverResult> _semanticHoverCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<ComputedHover>>> _inflightSemanticHover = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedStructuredResult> _structuredHoverCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedStructuredResult> _semanticStructuredHoverCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<QueryLensStructuredHoverResult?>>> _inflightSemanticStructuredHover = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _hoverCacheTtlMs;
    private readonly int _hoverCancellationGraceMs;
    private readonly int _hoverQueuedAdaptiveWaitMs;
    private readonly int _structuredQueuedAdaptiveWaitMs;
    private readonly bool _debugEnabled;

    public HoverHandler(DocumentManager documentManager, HoverPreviewService hoverPreviewService)
    {
        _documentManager = documentManager;
        _hoverPreviewService = hoverPreviewService;
        _hoverCacheTtlMs = ReadIntEnvironmentVariable(
            "QUERYLENS_HOVER_CACHE_TTL_MS",
            fallback: 15_000,
            min: 0,
            max: 120_000);
        _hoverCancellationGraceMs = ReadIntEnvironmentVariable(
            "QUERYLENS_HOVER_CANCEL_GRACE_MS",
            fallback: 350,
            min: 0,
            max: 5_000);
        _hoverQueuedAdaptiveWaitMs = ReadIntEnvironmentVariable(
            "QUERYLENS_MARKDOWN_QUEUE_ADAPTIVE_WAIT_MS",
            fallback: 200,
            min: 0,
            max: 2_000);
        _structuredQueuedAdaptiveWaitMs = ReadIntEnvironmentVariable(
            "QUERYLENS_STRUCTURED_QUEUE_ADAPTIVE_WAIT_MS",
            fallback: 200,
            min: 0,
            max: 2_000);
        _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);
    }

    public void HandleDaemonEvent(DaemonEvent daemonEvent)
    {
        switch (daemonEvent.EventCase)
        {
            case DaemonEvent.EventOneofCase.StateChanged:
                InvalidateCaches(
                    $"state-changed context={daemonEvent.StateChanged.ContextName} state={daemonEvent.StateChanged.State}");
                break;

            case DaemonEvent.EventOneofCase.ConfigReloaded:
                InvalidateCaches("config-reloaded");
                break;

            case DaemonEvent.EventOneofCase.AssemblyChanged:
                InvalidateCaches(
                    $"assembly-changed context={daemonEvent.AssemblyChanged.ContextName}");
                break;
        }
    }

    public void InvalidateForManualRecalculate()
    {
        InvalidateCaches("manual-recalculate");
    }
}
