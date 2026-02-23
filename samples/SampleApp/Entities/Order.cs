namespace SampleApp.Entities;

public class Order
{
    public int      Id        { get; set; }
    public int      UserId    { get; set; }
    public decimal  Total     { get; set; }
    public DateTime CreatedAt { get; set; }
    public string   Status    { get; set; } = default!;

    public User                  User  { get; set; } = default!;
    public ICollection<OrderItem> Items { get; set; } = [];
}
