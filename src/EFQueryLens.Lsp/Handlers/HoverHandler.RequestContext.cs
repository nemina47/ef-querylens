using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    private sealed record HoverRequestContext(
        string FilePath,
        string SourceText,
        int EffectiveLine,
        int EffectiveCharacter,
        SemanticHoverContext? SemanticContext,
        string CacheKey);

    private async Task<HoverRequestContext?> TryCreateRequestContextAsync(
        TextDocumentPositionParams request,
        CancellationToken cancellationToken,
        string requestLogPrefix,
        bool logNormalization)
    {
        var filePath = DocumentPathResolver.Resolve(request.TextDocument.Uri);
        var documentUri = request.TextDocument.Uri.ToString();
        var sourceText = await GetSourceTextAsync(documentUri, filePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return null;
        }

        LogHoverDebug($"{requestLogPrefix} path={filePath} line={request.Position.Line} char={request.Position.Character}");

        var hasSemanticContext = TryResolveSemanticHoverContext(
            filePath,
            sourceText,
            request.Position.Line,
            request.Position.Character,
            out var semanticContext);

        var effectiveLine = hasSemanticContext ? semanticContext!.EffectiveLine : request.Position.Line;
        var effectiveCharacter = hasSemanticContext ? semanticContext!.EffectiveCharacter : request.Position.Character;

        if (logNormalization
            && (effectiveLine != request.Position.Line || effectiveCharacter != request.Position.Character))
        {
            LogHoverDebug(
                $"hover-normalized from line={request.Position.Line} char={request.Position.Character} " +
                $"to line={effectiveLine} char={effectiveCharacter}");
        }

        var cacheKey = BuildHoverCacheKey(
            filePath,
            sourceText,
            effectiveLine,
            effectiveCharacter,
            semanticContext);

        return new HoverRequestContext(
            filePath,
            sourceText,
            effectiveLine,
            effectiveCharacter,
            semanticContext,
            cacheKey);
    }
}
