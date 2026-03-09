namespace EFQueryLens.Core;

public sealed record ModelInspectionRequest
{
    public required string AssemblyPath { get; init; }
    public string? DbContextTypeName { get; init; }
}
