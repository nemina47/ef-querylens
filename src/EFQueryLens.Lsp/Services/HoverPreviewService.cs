using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Handlers;

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
    string? ExecutedExpression,
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
    private const string HoverBuildMarker = "2026-03-28-vscode-linkfix";

    private readonly IQueryLensEngine _engine;
    private readonly WarmupHandler? _warmupHandler;
    private bool _debugEnabled;

    public HoverPreviewService(IQueryLensEngine engine, WarmupHandler? warmupHandler = null, bool debugEnabled = false)
    {
        _engine = engine;
        _warmupHandler = warmupHandler;
        _debugEnabled = debugEnabled;

        Console.Error.WriteLine(
            $"[QL-Hover] init: client='{Environment.GetEnvironmentVariable("QUERYLENS_CLIENT")}' " +
            $"build={HoverBuildMarker}");
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
