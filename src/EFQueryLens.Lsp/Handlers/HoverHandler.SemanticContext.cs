using EFQueryLens.Lsp.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
        string sourceText,
        int requestLine,
        int requestCharacter,
        SemanticHoverContext? semanticContext)
    {
        var fingerprint = AssemblyResolver.TryGetAssemblyFingerprint(filePath)
                          ?? $"no-assembly|{Path.GetFullPath(filePath)}";
        var fileToken = Path.GetFullPath(filePath).ToLowerInvariant();

        if (semanticContext is not null)
        {
            return $"{fingerprint}|file|{fileToken}|semantic|{semanticContext.SemanticKey}";
        }

        if (TryGetAnchorRangeAtPosition(
                sourceText,
                requestLine,
                requestCharacter,
                out var anchorStartLine,
                out var anchorStartCharacter,
                out var anchorEndLine,
                out var anchorEndCharacter))
        {
            return
                $"{fingerprint}|file|{fileToken}|anchor|{anchorStartLine}:{anchorStartCharacter}-{anchorEndLine}:{anchorEndCharacter}";
        }

        return $"{fingerprint}|file|{fileToken}|cursor|{requestLine}|{requestCharacter}";
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
        var factoryDbContextCandidates = (!string.IsNullOrWhiteSpace(targetAssembly)
                                         && !targetAssembly.StartsWith("DEBUG_FAIL", StringComparison.Ordinal))
            ? AssemblyResolver.TryExtractDbContextTypeNamesFromFactories(targetAssembly)
            : [];

        if (TryFindContainingChain(sourceText, line, character, out var containingChain))
        {
            var resolution = LspSyntaxHelper.BuildDbContextResolutionSnapshot(
                sourceText,
                containingChain.ContextVariableName,
                factoryDbContextCandidates);
            var dbContextTypeToken = LspSyntaxHelper.GetDbContextResolutionCacheToken(resolution);
            var anchorToken = $"{containingChain.StatementStartLine}:{containingChain.StatementStartCharacter}-{containingChain.StatementEndLine}:{containingChain.StatementEndCharacter}";
            semanticContext = new SemanticHoverContext(
                SemanticKey: $"{fingerprint}|{dbContextTypeToken}|{NormalizeWhitespace(containingChain.Expression)}|anchor|{anchorToken}",
                EffectiveLine: containingChain.StatementStartLine,
                EffectiveCharacter: containingChain.StatementStartCharacter);
            return true;
        }

        var sourceIndex = ProjectSourceHelper.GetProjectIndex(filePath);
        var expression = LspSyntaxHelper.TryExtractLinqExpression(sourceText, line, character, out var contextVariableName, out _, sourceIndex);

        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(contextVariableName))
        {
            return false;
        }

        var fallbackResolution = LspSyntaxHelper.BuildDbContextResolutionSnapshot(
            sourceText,
            contextVariableName,
            factoryDbContextCandidates);
        var fallbackDbContextToken = LspSyntaxHelper.GetDbContextResolutionCacheToken(fallbackResolution);
        if (!TryGetAnchorRangeAtPosition(sourceText, line, character, out var anchorStartLine, out var anchorStartChar, out var anchorEndLine, out var anchorEndChar))
        {
            anchorStartLine = line;
            anchorStartChar = character;
            anchorEndLine = line;
            anchorEndChar = character;
        }

        var fallbackAnchorToken = $"{anchorStartLine}:{anchorStartChar}-{anchorEndLine}:{anchorEndChar}";
        semanticContext = new SemanticHoverContext(
            SemanticKey: $"{fingerprint}|{fallbackDbContextToken}|{NormalizeWhitespace(expression)}|anchor|{fallbackAnchorToken}",
            EffectiveLine: anchorStartLine,
            EffectiveCharacter: anchorStartChar);
        return true;
    }

    private static bool TryGetAnchorRangeAtPosition(
        string sourceText,
        int line,
        int character,
        out int startLine,
        out int startCharacter,
        out int endLine,
        out int endCharacter)
    {
        startLine = line;
        startCharacter = character;
        endLine = line;
        endCharacter = character;

        try
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var text = tree.GetText();
            if (line < 0 || line >= text.Lines.Count)
                return false;

            var lineText = text.Lines[line];
            var boundedChar = Math.Min(Math.Max(character, 0), lineText.End - lineText.Start);
            var position = lineText.Start + boundedChar;
            var node = tree.GetRoot().FindToken(position).Parent;
            if (node is null)
                return false;

            SyntaxNode anchor = node.FirstAncestorOrSelf<InvocationExpressionSyntax>()
                               ?? node.FirstAncestorOrSelf<StatementSyntax>()
                               ?? node;

            while (anchor.Parent is MemberAccessExpressionSyntax or InvocationExpressionSyntax)
            {
                anchor = anchor.Parent;
            }

            var span = anchor.GetLocation().GetLineSpan();
            startLine = span.StartLinePosition.Line;
            startCharacter = span.StartLinePosition.Character;
            endLine = span.EndLinePosition.Line;
            endCharacter = span.EndLinePosition.Character;
            return true;
        }
        catch
        {
            return false;
        }
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
