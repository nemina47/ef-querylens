namespace EFQueryLens.Core.Contracts;

/// <summary>
/// Primary engine interface. CLI, MCP server, and IDE analyzer are thin hosts over this.
/// No UI, no transport, no provider references to belong here — only in the host layer.
/// </summary>
public interface IQueryLensEngine : IAsyncDisposable
{
    /// <summary>
    /// Translates a LINQ expression to SQL using offline execution-based capture.
    /// Does not require a real database connection.
    /// </summary>
    Task<QueryTranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Reflects the EF Core model (entities, columns, relationships, indexes)
    /// from the loaded assembly without running any queries.
    /// </summary>
    Task<ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request,
        CancellationToken ct = default);
}
