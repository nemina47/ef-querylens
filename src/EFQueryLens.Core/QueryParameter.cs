namespace EFQueryLens.Core;

public sealed record QueryParameter
{
    public required string Name { get; init; }
    public required string ClrType { get; init; }

    /// <summary>
    /// Inferred from expression literals when detectable; null otherwise.
    /// </summary>
    public string? InferredValue { get; init; }
}
