using Microsoft.EntityFrameworkCore;
using SampleApp.Entities;

namespace SampleApp;

public class AppDbContext : DbContext
{
    public DbSet<User>      Users      => Set<User>();
    public DbSet<Order>     Orders     => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Product>   Products   => Set<Product>();
    public DbSet<Category>  Categories => Set<Category>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.HasOne(o => o.User)
             .WithMany(u => u.Orders)
             .HasForeignKey(o => o.UserId);

            b.HasMany(o => o.Items)
             .WithOne(i => i.Order)
             .HasForeignKey(i => i.OrderId);
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.HasOne(i => i.Product)
             .WithMany(p => p.OrderItems)
             .HasForeignKey(i => i.ProductId);
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.HasOne(p => p.Category)
             .WithMany(c => c.Products)
             .HasForeignKey(p => p.CategoryId);
        });

        modelBuilder.Entity<Category>(b =>
        {
            b.HasOne(c => c.Parent)
             .WithMany(c => c.Children)
             .HasForeignKey(c => c.ParentCategoryId);
        });
    }
}
