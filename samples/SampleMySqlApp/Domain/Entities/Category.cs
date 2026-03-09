namespace SampleMySqlApp.Domain.Entities;

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ParentCategoryId { get; set; }

    public Category? Parent { get; set; }
    public ICollection<Category> Children { get; set; } = new List<Category>();
    public ICollection<Product> Products { get; set; } = new List<Product>();
}
