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
}
