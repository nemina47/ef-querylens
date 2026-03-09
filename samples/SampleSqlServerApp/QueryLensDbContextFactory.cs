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

namespace SampleSqlServerApp.Infrastructure.Persistence
{
    public sealed class SqlServerAppQueryLensFactory : EFQueryLens.Core.IQueryLensDbContextFactory<SqlServerAppDbContext>
    {
        public SqlServerAppDbContext CreateOfflineContext()
        {
            // SQL preview only needs provider metadata/model; no live DB call is made.
            var connectionString = "Server=ef_querylens_offline;Database=ef_querylens_offline;User Id=ef_querylens_offline;Password=ef_querylens_offline;TrustServerCertificate=True";

            var options = new DbContextOptionsBuilder<SqlServerAppDbContext>()
                .UseSqlServer(
                    connectionString,
                    sqlServer => sqlServer.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables()
                .Options;

            return new SqlServerAppDbContext(options);
        }
    }
}