using EntityFrameworkCore.Projectables;
using SampleMySqlApp.Domain.Enums;

namespace SampleMySqlApp.Domain.Entities;

public sealed class Order
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int CustomerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime CreatedUtc { get; set; }
    public decimal Total { get; set; }
    public OrderStatus Status { get; set; }
    public string? Notes { get; set; }
    public bool IsDeleted { get; set; }

    [Projectable]
    public bool IsNotDeleted => !IsDeleted;

    public User User { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}
