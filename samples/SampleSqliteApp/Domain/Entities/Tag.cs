namespace SampleSqliteApp.Domain.Entities;

public sealed class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public ICollection<CustomerTag> CustomerTags { get; set; } = new List<CustomerTag>();
}
