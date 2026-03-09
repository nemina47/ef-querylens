using EntityFrameworkCore.Projectables;

namespace SampleMySqlApp.Domain.Entities;

public sealed class Customer
{
    public int Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedUtc { get; set; }

    [Projectable]
    public bool IsNotDeleted => !IsDeleted;

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
