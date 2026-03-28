using Microsoft.EntityFrameworkCore;
using SamplePostgresApp.Application.Abstractions;
using SamplePostgresApp.Domain.Entities;
using SamplePostgresApp.Domain.Enums;

namespace SamplePostgresApp.Infrastructure.Persistence;

public sealed class PostgresAppDbContext : DbContext, IPostgresAppDbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();

    IQueryable<Customer> IPostgresAppDbContext.Customers => Customers.AsNoTracking();
    IQueryable<Order> IPostgresAppDbContext.Orders => Orders.AsNoTracking();

    public PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options)
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
            b.Property(x => x.IsDeleted).HasDefaultValue(false);
            b.Property(x => x.IsActive).HasDefaultValue(true);
            b.Property(x => x.CreatedUtc);

            // Global query filters: soft-delete + active-only.
            // EF QueryLens includes these automatically in every hover preview
            // unless you call .IgnoreQueryFilters() in your query.
            b.HasQueryFilter(x => !x.IsDeleted && x.IsActive);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(x => x.Id);
            b.Property(x => x.Total).HasColumnType("numeric(18,2)");
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

            // Soft-delete filter on orders.
            b.HasQueryFilter(x => !x.IsDeleted);
        });
    }
}
