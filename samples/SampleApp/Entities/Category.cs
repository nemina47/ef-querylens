namespace SampleApp.Entities;

public class Category
{
    public int      Id               { get; set; }
    public string   Name             { get; set; } = default!;

    // Self-referential parent relationship
    public int?     ParentCategoryId { get; set; }
    public Category? Parent          { get; set; }

    public ICollection<Category> Children  { get; set; } = [];
    public ICollection<Product>  Products  { get; set; } = [];
}
