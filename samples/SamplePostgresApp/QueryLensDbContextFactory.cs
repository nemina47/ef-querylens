using EntityFrameworkCore.Projectables;
using Microsoft.EntityFrameworkCore;

// ReSharper disable once CheckNamespace
namespace EFQueryLens.Core
{
    public interface IQueryLensDbContextFactory<out TContext>
        where TContext : DbContext
    {
        TContext CreateOfflineContext();
    }
}

namespace SamplePostgresApp.Infrastructure.Persistence
{
    public sealed class PostgresAppQueryLensFactory : EFQueryLens.Core.IQueryLensDbContextFactory<PostgresAppDbContext>
    {
        public PostgresAppDbContext CreateOfflineContext()
        {
            // Query preview only needs provider metadata/model; no live DB call is made.
            var connectionString = "Host=ef_querylens_offline;Database=ef_querylens_offline;Username=ef_querylens_offline;Password=ef_querylens_offline";

            var options = new DbContextOptionsBuilder<PostgresAppDbContext>()
                .UseNpgsql(
                    connectionString,
                    npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables()
                .Options;

            return new PostgresAppDbContext(options);
        }
    }
}
