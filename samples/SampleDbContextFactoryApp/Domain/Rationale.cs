namespace SampleDbContextFactoryApp.Domain;

public sealed class Rationale
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsDeleted { get; set; }
}
