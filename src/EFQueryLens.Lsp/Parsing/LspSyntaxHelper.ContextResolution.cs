using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    /// <summary>
    /// Scans <paramref name="sourceText"/> for a field, local variable, or parameter declaration
    /// whose name matches <paramref name="contextVariableName"/> and returns the declared type
    /// name string — suitable for populating <c>TranslationRequest.DbContextTypeName</c> to
    /// disambiguate when multiple DbContext types exist in the host assembly.
    ///
    /// Returns <c>null</c> when the variable cannot be found or its type cannot be determined
    /// syntactically (e.g. <c>var</c> with a complex initializer).
    /// </summary>
    internal static string? TryResolveDbContextTypeName(string sourceText, string contextVariableName)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(contextVariableName))
            return null;

        try
        {
            var root = CSharpSyntaxTree.ParseText(sourceText).GetRoot();

            string? resolved = null;

            // Fields: private readonly SlaPlusDbContext _db;
            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (field.Declaration.Variables.Any(v =>
                        v.Identifier.ValueText.Equals(contextVariableName, StringComparison.Ordinal)))
                {
                    resolved = field.Declaration.Type.ToString();
                    break;
                }
            }

            // Locals: var db = ...; SlaPlusDbContext db = ...;
            if (resolved is null)
            {
                foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    if (local.Declaration.Variables.Any(v =>
                            v.Identifier.ValueText.Equals(contextVariableName, StringComparison.Ordinal)))
                    {
                        resolved = local.Declaration.Type.ToString();
                        break;
                    }
                }
            }

            // Parameters: (SlaPlusDbContext db) or injected via ctor
            if (resolved is null)
            {
                foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
                {
                    if (parameter.Identifier.ValueText.Equals(contextVariableName, StringComparison.Ordinal)
                        && parameter.Type is not null)
                    {
                        resolved = parameter.Type.ToString();
                        break;
                    }
                }
            }

            return resolved is not null ? NormalizeDbContextTypeName(resolved) : null;
        }
        catch
        {
            // Best-effort — never propagate to caller.
        }

        return null;
    }

    /// <summary>
    /// Normalises a syntactically-resolved type name for use as a DbContext disambiguator.
    /// Strips nullable-reference-type annotations (<c>?</c>) — they have no CLR distinction.
    /// </summary>
    private static string NormalizeDbContextTypeName(string typeName) => typeName.TrimEnd('?');
}
