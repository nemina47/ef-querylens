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

    /// <summary>
    /// Generates the content and suggested file name for a
    /// <c>QueryLensDbContextFactory</c> file for the given DbContext type.
    /// The generated file is wrapped in <c>#if DEBUG</c>, marked
    /// <c>// &lt;auto-generated/&gt;</c>, and uses <c>Name=_querylens</c>
    /// connection strings so it is safe to commit without interfering with
    /// Release builds or static code analysers.
    /// </summary>
    Task<FactoryGenerationResult> GenerateFactoryAsync(
        FactoryGenerationRequest request,
        CancellationToken ct = default);
}
