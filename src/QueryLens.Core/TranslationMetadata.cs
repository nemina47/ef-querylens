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
    /// <c>"design-time-factory"</c> when an <c>IDesignTimeDbContextFactory&lt;T&gt;</c>
    /// was found in the user's assemblies; <c>"bootstrap"</c> when the provider
    /// bootstrap fallback was used instead.
    /// </summary>
    public string CreationStrategy { get; init; } = "bootstrap";
}
