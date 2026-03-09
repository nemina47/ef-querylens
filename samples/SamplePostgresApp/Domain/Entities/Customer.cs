using EntityFrameworkCore.Projectables;

namespace SamplePostgresApp.Domain.Entities;

public sealed class Customer
{
    public int Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedUtc { get; set; }

    [Projectable]
    public bool IsNotDeleted => !IsDeleted;

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
