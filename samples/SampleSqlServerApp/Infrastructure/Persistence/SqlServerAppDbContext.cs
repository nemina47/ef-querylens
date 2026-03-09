using Microsoft.EntityFrameworkCore;
using SampleSqlServerApp.Application.Abstractions;
using SampleSqlServerApp.Domain.Entities;
using SampleSqlServerApp.Domain.Enums;

namespace SampleSqlServerApp.Infrastructure.Persistence;

public class SqlServerAppDbContext : DbContext, ISqlServerAppDbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();

    IQueryable<Customer> ISqlServerAppDbContext.Customers => Customers.AsNoTracking();
    IQueryable<Order> ISqlServerAppDbContext.Orders => Orders.AsNoTracking();

    public SqlServerAppDbContext(DbContextOptions<SqlServerAppDbContext> options)
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
            b.Property(x => x.IsDeleted).HasDefaultValue(false);
            b.Property(x => x.IsActive).HasDefaultValue(true);
            b.Property(x => x.CreatedUtc);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("Orders");
            b.HasKey(x => x.Id);
            b.Property(x => x.Total).HasColumnType("decimal(18,2)");
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
    }
}
