using System.Collections.Concurrent;
using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Lsp.Services;

/// <summary>
/// Warms the hover cache by translating all LINQ chains in a document on a background
/// thread and writing the results into the hover cache via <see cref="_onPrewarmed"/>.
///
/// Triggered on <c>didOpen</c> and <c>didSave</c> immediately via <see cref="WarmDocument"/>,
/// and on <c>didChange</c> with a configurable debounce delay via
/// <see cref="DebounceWarmDocument"/> to avoid redundant translations while the user is typing.
/// </summary>
internal sealed class TranslationPrewarmService
{
    private readonly HoverPreviewService _hoverPreviewService;
    private readonly Action<string, string, int, int, CombinedHoverResult>? _onPrewarmed;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _debounceMs;

    public TranslationPrewarmService(
        HoverPreviewService hoverPreviewService,
        Action<string, string, int, int, CombinedHoverResult>? onPrewarmed = null)
    {
        _hoverPreviewService = hoverPreviewService;
        _onPrewarmed = onPrewarmed;
        _debounceMs = LspEnvironment.ReadInt(
            "QUERYLENS_CHANGE_PREWARM_DEBOUNCE_MS",
            fallback: 1_500,
            min: 0,
            max: 30_000);
    }

    /// <summary>
    /// Immediately fires a background warm for all LINQ chains in the document.
    /// Used by <c>didOpen</c> and <c>didSave</c>.
    /// </summary>
    public void WarmDocument(string filePath, string sourceText)
    {
        _ = Task.Run(() => WarmDocumentAsync(filePath, sourceText, CancellationToken.None));
    }

    /// <summary>
    /// Schedules a warm for the document after <c>QUERYLENS_CHANGE_PREWARM_DEBOUNCE_MS</c>
    /// milliseconds (default 1 500 ms).  If another call arrives for the same file before the
    /// delay elapses, the previous pending warm is cancelled and the timer resets.
    /// Used by <c>didChange</c>.  A zero debounce value disables change-triggered prewarming.
    /// </summary>
    public void DebounceWarmDocument(string filePath, string sourceText)
    {
        if (_debounceMs <= 0) return;

        // Cancel any previously scheduled warm for this file.
        if (_debounceTokens.TryRemove(filePath, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _debounceTokens[filePath] = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceMs, token);
                _debounceTokens.TryRemove(filePath, out _);
                await WarmDocumentAsync(filePath, sourceText, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Another keystroke arrived — this debounce window was superseded.
            }
            catch
            {
                // Best-effort — never surface pre-warm errors to the LSP host.
            }
        }, token);
    }

    private async Task WarmDocumentAsync(string filePath, string sourceText, CancellationToken ct)
    {
        try
        {
            var targetAssembly = AssemblyResolver.TryGetTargetAssembly(filePath);
            if (string.IsNullOrWhiteSpace(targetAssembly) || !File.Exists(targetAssembly))
                return;

            var chains = LspSyntaxHelper.FindAllLinqChains(sourceText);
            if (chains.Count == 0)
                return;

            foreach (var chain in chains)
            {
                if (ct.IsCancellationRequested) return;

                var combined = await _hoverPreviewService.BuildCombinedAsync(
                    filePath, sourceText, chain.Line, chain.Character, ct);

                _onPrewarmed?.Invoke(filePath, sourceText, chain.Line, chain.Character, combined);
            }
        }
        catch
        {
            // Best-effort — never surface pre-warm errors to the LSP host.
        }
    }
}
