namespace SampleSqliteApp.Domain.Entities;

/// <summary>Join entity for the Customer ↔ Tag many-to-many relationship.</summary>
public sealed class CustomerTag
{
    public int CustomerId { get; set; }
    public int TagId { get; set; }

    public Customer Customer { get; set; } = null!;
    public Tag Tag { get; set; } = null!;
}
