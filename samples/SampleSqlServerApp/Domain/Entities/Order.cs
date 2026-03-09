using EntityFrameworkCore.Projectables;
using SampleSqlServerApp.Domain.Enums;

namespace SampleSqlServerApp.Domain.Entities;

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public decimal Total { get; set; }
    public OrderStatus Status { get; set; }
    public string? Notes { get; set; }
    public bool IsDeleted { get; set; }

    [Projectable]
    public bool IsNotDeleted => !IsDeleted;

    public Customer Customer { get; set; } = null!;
}
