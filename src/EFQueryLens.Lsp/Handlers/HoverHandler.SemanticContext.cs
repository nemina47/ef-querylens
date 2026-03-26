using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    private string BuildHoverCacheKey(
        string filePath,
        string sourceText,
        int requestLine,
        int requestCharacter,
        SemanticHoverContext? semanticContext)
    {
        var sourceHash = StringComparer.Ordinal.GetHashCode(sourceText);

        if (semanticContext is not null)
        {
            return $"{Path.GetFullPath(filePath)}|semantic|{semanticContext.SemanticKey}|{semanticContext.EffectiveLine}|{semanticContext.EffectiveCharacter}|{sourceHash}";
        }

        return $"{Path.GetFullPath(filePath)}|cursor|{requestLine}|{requestCharacter}|{sourceHash}";
    }

    private static bool TryResolveSemanticHoverContext(
        string filePath,
        string sourceText,
        int line,
        int character,
        out SemanticHoverContext? semanticContext)
    {
        semanticContext = null;
        var projectKey = ProjectKeyHelper.GetProjectKey(filePath);

        if (TryFindContainingChain(sourceText, line, character, out var containingChain))
        {
            semanticContext = new SemanticHoverContext(
                SemanticKey: $"{projectKey}|{containingChain.ContextVariableName.Trim()}|{NormalizeWhitespace(containingChain.Expression)}",
                EffectiveLine: containingChain.Line,
                EffectiveCharacter: containingChain.Character);
            return true;
        }

        var expression = LspSyntaxHelper.TryExtractLinqExpression(sourceText, line, character, out var contextVariableName);

        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(contextVariableName))
        {
            return false;
        }

        semanticContext = new SemanticHoverContext(
            SemanticKey: $"{projectKey}|{contextVariableName.Trim()}|{NormalizeWhitespace(expression)}",
            EffectiveLine: line,
            EffectiveCharacter: character);
        return true;
    }

    private static bool TryFindContainingChain(string sourceText, int line, int character, out LinqChainInfo containingChain)
    {
        containingChain = null!;

        foreach (var chain in LspSyntaxHelper.FindAllLinqChains(sourceText))
        {
            if (!IsWithinStatementRange(chain, line, character))
            {
                continue;
            }

            containingChain = chain;
            return true;
        }

        return false;
    }

    private static bool IsWithinStatementRange(LinqChainInfo chain, int line, int character)
    {
        if (line < chain.StatementStartLine || line > chain.StatementEndLine)
        {
            return false;
        }

        if (chain.StatementStartLine == chain.StatementEndLine)
        {
            return character >= chain.StatementStartCharacter && character <= chain.StatementEndCharacter;
        }

        if (line == chain.StatementStartLine)
        {
            return character >= chain.StatementStartCharacter;
        }

        if (line == chain.StatementEndLine)
        {
            return character <= chain.StatementEndCharacter;
        }

        return true;
    }

    private static string NormalizeWhitespace(string value)
    {
        var buffer = new char[value.Length];
        var index = 0;
        var previousWasWhitespace = false;

        foreach (var current in value)
        {
            if (char.IsWhiteSpace(current))
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                buffer[index++] = ' ';
                previousWasWhitespace = true;
            }
            else
            {
                buffer[index++] = current;
                previousWasWhitespace = false;
            }
        }

        return new string(buffer, 0, index).Trim();
    }

    private static string BuildInFlightKey(string filePath, SemanticHoverContext semanticContext) =>
        $"{Path.GetFullPath(filePath)}|{semanticContext.SemanticKey}|{semanticContext.EffectiveLine}|{semanticContext.EffectiveCharacter}";

    private sealed record SemanticHoverContext(string SemanticKey, int EffectiveLine, int EffectiveCharacter);
}
