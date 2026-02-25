namespace QueryLens.Core;

public sealed record TranslationMetadata
{
    public string   DbContextType       { get; init; } = default!;
    public string   EfCoreVersion       { get; init; } = default!;
    public string   ProviderName        { get; init; } = default!;
    public TimeSpan TranslationTime     { get; init; }

    /// <summary>
    /// True when EF Core silently evaluated part of the query on the client.
    /// Always flag this — it is a silent performance killer.
    /// </summary>
    public bool HasClientEvaluation { get; init; }

    /// <summary>
    /// How the offline DbContext instance was created.
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>"querylens-factory"</c> — an <c>IQueryLensDbContextFactory&lt;T&gt;</c>
    ///     was found (highest priority; user-controlled offline configuration).
    ///   </description></item>
    ///   <item><description>
    ///     <c>"design-time-factory"</c> — an <c>IDesignTimeDbContextFactory&lt;T&gt;</c>
    ///     was found (EF Core tooling pattern).
    ///   </description></item>
    ///   <item><description>
    ///     <c>"bootstrap"</c> — automatic fallback using the provider's fake connection string.
    ///   </description></item>
    /// </list>
    /// </summary>
    public string CreationStrategy { get; init; } = "bootstrap";
}
