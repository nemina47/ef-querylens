namespace QueryLens.Core;

public record TranslationRequest
{
    /// <summary>
    /// LINQ expression as C# source text.
    /// Example: "db.Orders.Where(o => o.UserId == 5).Include(o => o.Items)"
    /// </summary>
    public required string Expression          { get; init; }

    public required string AssemblyPath        { get; init; }

    /// <summary>null = auto-discover the first DbContext found in the assembly.</summary>
    public string?         DbContextTypeName   { get; init; }

    /// <summary>Name of the DbContext variable in the script globals. Defaults to "db".</summary>
    public string          ContextVariableName { get; init; } = "db";
}
