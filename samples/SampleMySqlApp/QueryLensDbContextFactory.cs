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

namespace SampleMySqlApp.Infrastructure.Persistence
{
    public sealed class MySqlAppQueryLensFactory : EFQueryLens.Core.IQueryLensDbContextFactory<MySqlAppDbContext>
    {
        public MySqlAppDbContext CreateOfflineContext()
        {
            // Query preview only needs provider metadata/model; no live DB call is made.
            var connectionString = "Server=ef_querylens_offline;Database=ef_querylens_offline;User Id=ef_querylens_offline;Password=ef_querylens_offline";

            var options = new DbContextOptionsBuilder<MySqlAppDbContext>()
                .UseMySql(
                    connectionString,
                    new MySqlServerVersion(new Version(8, 0, 36)),
                    mySql => mySql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables()
                .Options;

            return new MySqlAppDbContext(options);
        }
    }
}