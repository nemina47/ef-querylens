using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Lsp.Services;

internal sealed record CombinedHoverResult(
    HoverPreviewComputationResult Markdown,
    QueryLensStructuredHoverResult Structured);

internal sealed record HoverPreviewComputationResult(
    bool Success,
    string Output,
    QueryTranslationStatus Status = QueryTranslationStatus.Ready,
    double AvgTranslationMs = 0,
    double LastTranslationMs = 0);

internal sealed record QueryLensSqlStatement(string Sql, string? SplitLabel);

internal sealed record QueryLensStructuredHoverResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<QueryLensSqlStatement> Statements,
    int CommandCount,
    string? SourceExpression,
    string? DbContextType,
    string? ProviderName,
    string? SourceFile,
    int SourceLine,
    IReadOnlyList<string> Warnings,
    string? EnrichedSql,
    string? Mode,
    QueryTranslationStatus Status = QueryTranslationStatus.Ready,
    string? StatusMessage = null,
    double AvgTranslationMs = 0,
    double LastTranslationMs = 0);

internal sealed partial class HoverPreviewService
{
    private readonly IQueryLensEngine _engine;
    private readonly bool _useBrowserSafeHoverActionLinks;
    private bool _debugEnabled;

    public HoverPreviewService(IQueryLensEngine engine, bool debugEnabled = false)
    {
        _engine = engine;
        _useBrowserSafeHoverActionLinks = string.Equals(
            Environment.GetEnvironmentVariable("QUERYLENS_CLIENT"),
            "rider",
            StringComparison.OrdinalIgnoreCase);
        _debugEnabled = debugEnabled;
    }

    internal void SetDebugEnabled(bool enabled)
    {
        _debugEnabled = enabled;
    }

    private void LogDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-Hover] {message}");
    }
}
