using System.Collections.Concurrent;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    private readonly DocumentManager _documentManager;
    private readonly HoverPreviewService _hoverPreviewService;
    private readonly ConcurrentDictionary<string, CachedEntry> _hoverCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedEntry> _semanticHoverCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<ComputedEntry>>> _inflightSemanticHover = new(StringComparer.OrdinalIgnoreCase);
    private int _hoverCacheTtlMs;
    private int _hoverCancellationGraceMs;
    private int _hoverQueuedAdaptiveWaitMs;
    private int _structuredQueuedAdaptiveWaitMs;
    private bool _debugEnabled;

    public HoverHandler(DocumentManager documentManager, HoverPreviewService hoverPreviewService)
    {
        _documentManager = documentManager;
        _hoverPreviewService = hoverPreviewService;
        _hoverCacheTtlMs = LspEnvironment.ReadInt(
            "QUERYLENS_HOVER_CACHE_TTL_MS",
            fallback: 15_000,
            min: 0,
            max: 120_000);
        _hoverCancellationGraceMs = LspEnvironment.ReadInt(
            "QUERYLENS_HOVER_CANCEL_GRACE_MS",
            fallback: 350,
            min: 0,
            max: 5_000);
        _hoverQueuedAdaptiveWaitMs = LspEnvironment.ReadInt(
            "QUERYLENS_MARKDOWN_QUEUE_ADAPTIVE_WAIT_MS",
            fallback: 200,
            min: 0,
            max: 2_000);
        _structuredQueuedAdaptiveWaitMs = LspEnvironment.ReadInt(
            "QUERYLENS_STRUCTURED_QUEUE_ADAPTIVE_WAIT_MS",
            fallback: 200,
            min: 0,
            max: 2_000);
        _debugEnabled = LspEnvironment.ReadBool("QUERYLENS_DEBUG", fallback: false);
    }

    /// <summary>
    /// Called when the watched assembly file changes on disk (recompile detected).
    /// Evicts all hover caches so the next hover fetches fresh SQL.
    /// </summary>
    public void OnAssemblyChanged()
    {
        InvalidateCaches("assembly-changed");
    }

    public void InvalidateForManualRecalculate()
    {
        InvalidateCaches("manual-recalculate");
    }

    public void InvalidateForConfigurationChange()
    {
        InvalidateCaches("configuration-changed");
    }

    public void ApplyClientConfiguration(LspClientConfiguration configuration)
    {
        if (configuration.DebugEnabled.HasValue)
        {
            _debugEnabled = configuration.DebugEnabled.Value;
            _hoverPreviewService.SetDebugEnabled(_debugEnabled);
        }

        if (configuration.HoverCacheTtlMs.HasValue)
        {
            _hoverCacheTtlMs = configuration.HoverCacheTtlMs.Value;
        }

        if (configuration.HoverCancelGraceMs.HasValue)
        {
            _hoverCancellationGraceMs = configuration.HoverCancelGraceMs.Value;
        }

        if (configuration.MarkdownQueueAdaptiveWaitMs.HasValue)
        {
            _hoverQueuedAdaptiveWaitMs = configuration.MarkdownQueueAdaptiveWaitMs.Value;
        }

        if (configuration.StructuredQueueAdaptiveWaitMs.HasValue)
        {
            _structuredQueuedAdaptiveWaitMs = configuration.StructuredQueueAdaptiveWaitMs.Value;
        }
    }
}
