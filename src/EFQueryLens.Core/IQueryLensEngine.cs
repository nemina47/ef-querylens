namespace EFQueryLens.Core;

/// <summary>
/// Primary engine interface. CLI, MCP server, and IDE analyzer are thin hosts over this.
/// No UI, no transport, no provider references belong here — only in the host layer.
/// </summary>
public interface IQueryLensEngine : IAsyncDisposable
{
    /// <summary>
    /// Translates a LINQ expression to SQL using ToQueryString().
    /// Does not require a real database connection.
    /// </summary>
    Task<QueryTranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Queues or retrieves a translation result without forcing callers to block.
    /// Hosts can poll this method until status becomes <see cref="QueryTranslationStatus.Ready"/>.
    /// </summary>
    Task<QueuedTranslationResult> TranslateQueuedAsync(
        TranslationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Runs EXPLAIN (ANALYZE) against a real database and returns the normalized plan.
    /// Requires a live connection string.
    /// </summary>
    Task<ExplainResult> ExplainAsync(
        ExplainRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Reflects the EF Core model (entities, columns, relationships, indexes)
    /// from the loaded assembly without running any queries.
    /// </summary>
    Task<ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request,
        CancellationToken ct = default);
}
