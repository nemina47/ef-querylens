namespace SampleApp.Entities;

public class User
{
    public int      Id        { get; set; }
    public string   Name      { get; set; } = default!;
    public string   Email     { get; set; } = default!;
    public DateTime CreatedAt { get; set; }

    public ICollection<Order> Orders { get; set; } = [];
}
