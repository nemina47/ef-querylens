namespace SampleApp.Entities;

public class Product
{
    public int      Id         { get; set; }
    public string   Name       { get; set; } = default!;
    public int      CategoryId { get; set; }
    public decimal  Price      { get; set; }
    public int      Stock      { get; set; }

    public Category Category  { get; set; } = default!;
    public ICollection<OrderItem> OrderItems { get; set; } = [];
}
