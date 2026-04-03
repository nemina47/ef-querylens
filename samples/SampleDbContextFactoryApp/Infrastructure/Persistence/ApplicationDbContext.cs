using Microsoft.EntityFrameworkCore;
using SampleDbContextFactoryApp.Domain;

namespace SampleDbContextFactoryApp.Infrastructure.Persistence;

public sealed class ApplicationDbContext : DbContext
{
    public DbSet<Rationale> Rationales => Set<Rationale>();

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Rationale>(entity =>
        {
            entity.ToTable("Rationales");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.Property(x => x.IsDeleted).HasDefaultValue(false);
        });
    }
}
