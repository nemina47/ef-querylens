namespace EFQueryLens.Core.Contracts;

/// <summary>
/// Result of <see cref="IQueryLensEngine.GenerateFactoryAsync"/>.
/// </summary>
public sealed record FactoryGenerationResult
{
    /// <summary>Generated C# source content for the factory file.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// Suggested file name, e.g. <c>"AppDbContextQueryLensFactory.cs"</c>.
    /// </summary>
    public required string SuggestedFileName { get; init; }

    /// <summary>
    /// Fully-qualified name of the DbContext the factory was generated for.
    /// </summary>
    public required string DbContextTypeFullName { get; init; }
}
