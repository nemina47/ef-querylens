namespace SampleMySqlApp.Domain.Entities;

public sealed class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
