using SampleSqliteApp.Domain.Enums;

namespace SampleSqliteApp.Domain.Entities;

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
    public OrderStatus Status { get; set; }
    public string? Notes { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedUtc { get; set; }

    public Customer Customer { get; set; } = null!;
}
