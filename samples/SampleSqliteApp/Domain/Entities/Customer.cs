namespace SampleSqliteApp.Domain.Entities;

public sealed class Customer
{
    public int Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedUtc { get; set; }

    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<CustomerTag> CustomerTags { get; set; } = new List<CustomerTag>();
}
