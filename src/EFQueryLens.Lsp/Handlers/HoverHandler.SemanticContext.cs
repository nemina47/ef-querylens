using EFQueryLens.Lsp.Parsing;
using Microsoft.CodeAnalysis;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class HoverHandler
{
    /// <summary>
    /// Builds the primary hover cache key.
    ///
    /// The key is scoped by <b>assembly fingerprint</b> (path + size + last-write timestamp)
    /// rather than a hash of the full source text. This means:
    /// <list type="bullet">
    ///   <item>Edits anywhere in the file do <em>not</em> bust cached hover entries.</item>
    ///   <item>A rebuild (new .dll on disk) automatically invalidates all entries because
    ///         the fingerprint changes.</item>
    /// </list>
    /// </summary>
    private string BuildHoverCacheKey(
        string filePath,
        int requestLine,
        int requestCharacter,
        SemanticHoverContext? semanticContext)
    {
        var fingerprint = AssemblyResolver.TryGetAssemblyFingerprint(filePath)
                          ?? $"no-assembly|{Path.GetFullPath(filePath)}";

        if (semanticContext is not null)
        {
            return $"{fingerprint}|semantic|{semanticContext.SemanticKey}|{semanticContext.EffectiveLine}|{semanticContext.EffectiveCharacter}";
        }

        return $"{fingerprint}|cursor|{requestLine}|{requestCharacter}";
    }

    private static bool TryResolveSemanticHoverContext(
        string filePath,
        string sourceText,
        int line,
        int character,
        out SemanticHoverContext? semanticContext)
    {
        semanticContext = null;

        // Resolve the assembly fingerprint and DbContext type once — both are needed
        // to build a semantic key that can be matched by the prewarm service (Phase 4).
        var fingerprint = AssemblyResolver.TryGetAssemblyFingerprint(filePath)
                          ?? $"no-assembly|{Path.GetFullPath(filePath)}";

        var targetAssembly = AssemblyResolver.TryGetTargetAssembly(filePath);
        var dbContextType = (!string.IsNullOrWhiteSpace(targetAssembly)
                             && !targetAssembly.StartsWith("DEBUG_FAIL", StringComparison.Ordinal))
            ? AssemblyResolver.TryExtractDbContextTypeFromFactory(targetAssembly)
            : null;

        if (TryFindContainingChain(sourceText, line, character, out var containingChain))
        {
            dbContextType ??= LspSyntaxHelper.TryResolveDbContextTypeName(sourceText, containingChain.ContextVariableName);
            semanticContext = new SemanticHoverContext(
                SemanticKey: $"{fingerprint}|{dbContextType ?? string.Empty}|{NormalizeWhitespace(containingChain.Expression)}",
                EffectiveLine: containingChain.Line,
                EffectiveCharacter: containingChain.Character);
            return true;
        }

        var siblingRoots = ProjectSourceHelper.GetSiblingRoots(filePath);
        var expression = LspSyntaxHelper.TryExtractLinqExpression(sourceText, line, character, out var contextVariableName, siblingRoots);

        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(contextVariableName))
        {
            return false;
        }

        dbContextType ??= LspSyntaxHelper.TryResolveDbContextTypeName(sourceText, contextVariableName);
        semanticContext = new SemanticHoverContext(
            SemanticKey: $"{fingerprint}|{dbContextType ?? string.Empty}|{NormalizeWhitespace(expression)}",
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

    internal static string NormalizeWhitespace(string value)
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

    private sealed record SemanticHoverContext(string SemanticKey, int EffectiveLine, int EffectiveCharacter);
}
