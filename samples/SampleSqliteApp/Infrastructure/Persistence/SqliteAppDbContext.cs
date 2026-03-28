using Microsoft.EntityFrameworkCore;
using SampleSqliteApp.Application.Abstractions;
using SampleSqliteApp.Domain.Entities;
using SampleSqliteApp.Domain.Enums;

namespace SampleSqliteApp.Infrastructure.Persistence;

public sealed class SqliteAppDbContext : DbContext, ISqliteAppDbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<CustomerTag> CustomerTags => Set<CustomerTag>();

    IQueryable<Customer> ISqliteAppDbContext.Customers => Customers.AsNoTracking();
    IQueryable<Order> ISqliteAppDbContext.Orders => Orders.AsNoTracking();
    IQueryable<Tag> ISqliteAppDbContext.Tags => Tags.AsNoTracking();
    IQueryable<CustomerTag> ISqliteAppDbContext.CustomerTags => CustomerTags.AsNoTracking();

    public SqliteAppDbContext(DbContextOptions<SqliteAppDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customers");
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
            b.ToTable("orders");
            b.HasKey(x => x.Id);
            b.Property(x => x.Total);
            b.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(OrderStatus.Pending);
            b.Property(x => x.Notes).HasMaxLength(1024);
            b.Property(x => x.IsDeleted).HasDefaultValue(false);
            b.HasIndex(x => new { x.CustomerId, x.CreatedUtc });
            b.HasOne(x => x.Customer)
                .WithMany(c => c.Orders)
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Tag>(b =>
        {
            b.ToTable("tags");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(100);
            b.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<CustomerTag>(b =>
        {
            b.ToTable("customer_tags");
            b.HasKey(x => new { x.CustomerId, x.TagId });
            b.HasOne(x => x.Customer)
                .WithMany(c => c.CustomerTags)
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Tag)
                .WithMany(t => t.CustomerTags)
                .HasForeignKey(x => x.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
