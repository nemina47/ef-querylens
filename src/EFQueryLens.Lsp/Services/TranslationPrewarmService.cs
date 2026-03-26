using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Engine;
using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Lsp.Services;

/// <summary>
/// Fires background translate requests for all LINQ chains in a document so that
/// hover requests hit the daemon cache instead of waiting for a cold translation.
/// </summary>
internal sealed class TranslationPrewarmService
{
    private readonly IEngineControl _engine;

    public TranslationPrewarmService(IEngineControl engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Scans <paramref name="sourceText"/> for LINQ chains and fires a warm request
    /// for each one. Returns immediately — all work is fire-and-forget.
    /// </summary>
    public void WarmDocument(string filePath, string sourceText)
    {
        _ = Task.Run(() => WarmDocumentAsync(filePath, sourceText, CancellationToken.None));
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

            var usingContext = LspSyntaxHelper.ExtractUsingContext(sourceText);

            foreach (var chain in chains)
            {
                var localTypes = LspSyntaxHelper.ExtractLocalVariableTypesAtPosition(
                    sourceText, chain.Line, chain.Character);

                var request = new TranslationRequest
                {
                    AssemblyPath = targetAssembly,
                    Expression = chain.Expression,
                    ContextVariableName = chain.ContextVariableName,
                    AdditionalImports = usingContext.Imports.ToArray(),
                    UsingAliases = new Dictionary<string, string>(usingContext.Aliases, StringComparer.Ordinal),
                    UsingStaticTypes = usingContext.StaticTypes.ToArray(),
                    LocalVariableTypes = localTypes,
                };

                await _engine.WarmTranslateAsync(request, ct);
            }
        }
        catch
        {
            // Best-effort — never surface pre-warm errors to the LSP host.
        }
    }
}
