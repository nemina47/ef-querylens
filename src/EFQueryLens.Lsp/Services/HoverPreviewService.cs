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
    private readonly IQueryLensEngine _engine;
    private readonly bool _useBrowserSafeHoverActionLinks;
    private readonly int _actionPort;
    private bool _debugEnabled;

    public HoverPreviewService(IQueryLensEngine engine, bool debugEnabled = false)
    {
        _engine = engine;
        var client = Environment.GetEnvironmentVariable("QUERYLENS_CLIENT");
        _useBrowserSafeHoverActionLinks =
            string.Equals(client, "rider", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(client, "vscode", StringComparison.OrdinalIgnoreCase);
        _actionPort = int.TryParse(
            Environment.GetEnvironmentVariable("QUERYLENS_ACTION_PORT"),
            out var port) ? port : 0;
        _debugEnabled = debugEnabled;

        Console.Error.WriteLine(
            $"[QL-Hover] init: client='{Environment.GetEnvironmentVariable("QUERYLENS_CLIENT")}' " +
            $"useBrowserSafe={_useBrowserSafeHoverActionLinks} actionPort={_actionPort} " +
            $"linkScheme={(_actionPort > 0 ? $"http://127.0.0.1:{_actionPort}" : "efquerylens://")}");
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
