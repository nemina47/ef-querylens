using Microsoft.EntityFrameworkCore;
using SampleMySqlApp.Application.Abstractions;
using SampleMySqlApp.Domain.Entities;
using SampleMySqlApp.Domain.Enums;

namespace SampleMySqlApp.Infrastructure.Persistence;

public sealed class MySqlAppDbContext : DbContext, IMySqlAppDbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<User> Users => Set<User>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ApplicationChecklist> ApplicationChecklists => Set<ApplicationChecklist>();
    public DbSet<ApplicationChecklistChangeType> ApplicationChecklistChangeTypes => Set<ApplicationChecklistChangeType>();

    IQueryable<Customer> IMySqlAppDbContext.Customers => Customers.AsNoTracking();
    IQueryable<Order> IMySqlAppDbContext.Orders => Orders.AsNoTracking();
    IQueryable<User> IMySqlAppDbContext.Users => Users.AsNoTracking();
    IQueryable<OrderItem> IMySqlAppDbContext.OrderItems => OrderItems.AsNoTracking();
    IQueryable<Product> IMySqlAppDbContext.Products => Products.AsNoTracking();
    IQueryable<Category> IMySqlAppDbContext.Categories => Categories.AsNoTracking();
    IQueryable<ApplicationChecklist> IMySqlAppDbContext.ApplicationChecklists => ApplicationChecklists.AsNoTracking();
    IQueryable<ApplicationChecklistChangeType> IMySqlAppDbContext.ApplicationChecklistChangeTypes => ApplicationChecklistChangeTypes.AsNoTracking();

    public MySqlAppDbContext(DbContextOptions<MySqlAppDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("Customers");
            b.HasKey(x => x.Id);
            b.Property(x => x.CustomerId);
            b.HasIndex(x => x.CustomerId).IsUnique();
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Email).HasMaxLength(320);
            b.Property(x => x.IsActive).HasDefaultValue(true);
            b.Property(x => x.IsDeleted).HasDefaultValue(false);
            b.Property(x => x.CreatedUtc);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("Orders");
            b.HasKey(x => x.Id);
            b.Property(x => x.Total).HasColumnType("decimal(18,2)");
            b.Property(x => x.CreatedAt);
            b.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(OrderStatus.Pending);
            b.Property(x => x.Notes).HasMaxLength(1024);
            b.Property(x => x.IsDeleted).HasDefaultValue(false);
            b.HasIndex(x => new { x.CustomerId, x.CreatedUtc });
            b.HasOne(x => x.User)
                .WithMany(u => u.Orders)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.Customer)
                .WithMany(c => c.Orders)
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Items)
                .WithOne(i => i.Order)
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Email).HasMaxLength(320);
        });

        modelBuilder.Entity<OrderItem>(b =>
        {
            b.ToTable("OrderItems");
            b.HasKey(x => x.Id);
            b.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
            b.HasOne(x => x.Product)
                .WithMany(p => p.OrderItems)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("Products");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(256);
            b.Property(x => x.Price).HasColumnType("decimal(18,2)");
            b.HasOne(x => x.Category)
                .WithMany(c => c.Products)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Category>(b =>
        {
            b.ToTable("Categories");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(256);
            b.HasOne(x => x.Parent)
                .WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentCategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApplicationChecklist>(b =>
        {
            b.ToTable("ApplicationChecklists");
            b.HasKey(x => x.Id);
            b.HasMany(x => x.ChecklistChangeTypes)
                .WithOne(x => x.ApplicationChecklist)
                .HasForeignKey(x => x.ApplicationChecklistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApplicationChecklistChangeType>(b =>
        {
            b.ToTable("ApplicationChecklistChangeTypes");
            b.HasKey(x => x.Id);
            b.Property(x => x.ChangeType).HasMaxLength(128);
        });
    }
}
