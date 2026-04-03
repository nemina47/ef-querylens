using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Services;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    /// <summary>
    /// Stores a pre-warmed translation result directly into the hover cache, keyed by the
    /// same fingerprint + semantic key that <see cref="HandleAsync"/> and
    /// <see cref="HandleStructuredAsync"/> would compute for the same position.
    ///
    /// Called by <see cref="TranslationPrewarmService"/> after it has successfully translated
    /// a LINQ chain on a background thread so the first hover at that position is a cache hit.
    ///
    /// Only <see cref="QueryTranslationStatus.Ready"/> results with a non-null hover or
    /// structured value are stored.  Existing Ready entries at the same key are not replaced.
    /// </summary>
    internal void StorePrewarmedEntry(
        string filePath,
        string sourceText,
        int line,
        int character,
        CombinedHoverResult combined)
    {
        if (!IsCacheEnabled()) return;

        var computed = BuildComputedFromCombined(combined);

        // Only cache successful translations — skip errors, InQueue placeholders, etc.
        if (computed.Status is not QueryTranslationStatus.Ready) return;
        if (computed.Hover is null && computed.Structured is null) return;

        TryResolveSemanticHoverContext(filePath, sourceText, line, character, out var semanticContext);
        var cacheKey = BuildHoverCacheKey(filePath, sourceText, line, character, semanticContext);

        // Don't overwrite a Ready entry that a real hover already computed.
        if (TryGetCachedEntry(cacheKey, out var existing)
            && existing!.Status is QueryTranslationStatus.Ready)
        {
            LogHoverDebug($"prewarm-skip-existing line={line} char={character}");
            return;
        }

        CacheEntry(cacheKey, computed, semanticContext);
        LogHoverDebug($"prewarm-stored line={line} char={character}");
    }
}
