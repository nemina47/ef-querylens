namespace EFQueryLens.Core;

public sealed record ModelSnapshot
{
    public required string DbContextType { get; init; }
    public IReadOnlyList<string> DbSetProperties { get; init; } = [];
    public IReadOnlyList<EntitySnapshot> Entities { get; init; } = [];
}

public sealed record EntitySnapshot
{
    public required string ClrType { get; init; }
    public required string TableName { get; init; }
    public IReadOnlyList<PropertySnapshot> Properties { get; init; } = [];
    public IReadOnlyList<NavigationSnapshot> Navigations { get; init; } = [];
    public IReadOnlyList<IndexSnapshot> Indexes { get; init; } = [];
}

public sealed record PropertySnapshot
{
    public required string Name { get; init; }
    public required string ClrType { get; init; }
    public required string ColumnName { get; init; }
    public bool IsKey { get; init; }
    public bool IsNullable { get; init; }
}

public sealed record NavigationSnapshot
{
    public required string Name { get; init; }
    public required string TargetEntity { get; init; }
    public required bool IsCollection { get; init; }
    public string? ForeignKey { get; init; }
}

public sealed record IndexSnapshot
{
    public required IReadOnlyList<string> Columns { get; init; }
    public bool IsUnique { get; init; }
    public string? Name { get; init; }
}
