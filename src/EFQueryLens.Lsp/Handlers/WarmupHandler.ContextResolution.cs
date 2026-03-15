using EFQueryLens.Lsp.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Handlers;

internal sealed partial class WarmupHandler
{
    private static bool IsMultipleDbContextAmbiguity(Exception ex)
    {
        return ex.Message.Contains("Multiple DbContext types found", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryResolveDbContextTypeName(string sourceText, int line, int character)
    {
        _ = LspSyntaxHelper.TryExtractLinqExpression(sourceText, line, character, out var contextVariableName);
        if (string.IsNullOrWhiteSpace(contextVariableName))
        {
            return null;
        }

        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetRoot();

        // Prefer explicit fields/locals/parameters named as the context variable.
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (field.Declaration.Variables.Any(v => v.Identifier.ValueText.Equals(contextVariableName, StringComparison.Ordinal)))
            {
                return field.Declaration.Type.ToString();
            }
        }

        foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (local.Declaration.Variables.Any(v => v.Identifier.ValueText.Equals(contextVariableName, StringComparison.Ordinal)))
            {
                return local.Declaration.Type.ToString();
            }
        }

        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Identifier.ValueText.Equals(contextVariableName, StringComparison.Ordinal)
                && parameter.Type is not null)
            {
                return parameter.Type.ToString();
            }
        }

        return null;
    }

    private async Task<string?> GetSourceTextAsync(string documentUri, string filePath, CancellationToken cancellationToken)
    {
        var sourceText = _documentManager.GetDocumentText(documentUri);
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            return sourceText;
        }

        if (!File.Exists(filePath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(filePath, cancellationToken);
    }
}
