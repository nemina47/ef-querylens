namespace EFQueryLens.Core.Contracts;

/// <summary>
/// Input for <see cref="IQueryLensEngine.GenerateFactoryAsync"/>.
/// </summary>
public sealed record FactoryGenerationRequest
{
    /// <summary>
    /// Path to the compiled executable assembly that QueryLens is targeting.
    /// </summary>
    public required string AssemblyPath { get; init; }

    /// <summary>
    /// Fully-qualified name of the DbContext type to generate a factory for.
    /// When <see langword="null"/> the first discovered DbContext is used.
    /// </summary>
    public string? DbContextTypeName { get; init; }
}
