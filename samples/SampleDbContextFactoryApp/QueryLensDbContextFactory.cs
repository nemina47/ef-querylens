using Microsoft.EntityFrameworkCore;
using SampleDbContextFactoryApp.Infrastructure.Persistence;

// ReSharper disable once CheckNamespace
namespace EFQueryLens.Core
{
    public interface IQueryLensDbContextFactory<out TContext>
        where TContext : DbContext
    {
        TContext CreateOfflineContext();
    }
}

namespace SampleDbContextFactoryApp.Infrastructure.Persistence
{
    public sealed class ApplicationDbContextFactory :
        EFQueryLens.Core.IQueryLensDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateOfflineContext()
        {
            return new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite("Data Source=sample-factory-offline.db")
                .Options);
        }
    }
}
