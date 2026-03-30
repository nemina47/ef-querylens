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
    public sealed class SqlServerAppDbContextFactory :
        EFQueryLens.Core.IQueryLensDbContextFactory<SqlServerAppDbContext>
    {
        public SqlServerAppDbContext CreateOfflineContext()
        {
            return new SqlServerAppDbContext(CreateSqlServerOptions<SqlServerAppDbContext>());
        }

        private static DbContextOptions<TContext> CreateSqlServerOptions<TContext>()
            where TContext : DbContext
        {
            var connectionString = "Server=ef_querylens_offline;Database=ef_querylens_offline;User Id=ef_querylens_offline;Password=ef_querylens_offline;TrustServerCertificate=True";
            return new DbContextOptionsBuilder<TContext>()
                .UseSqlServer(connectionString,
                    sqlServer => sqlServer.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables()
                .Options;
        }
    }

    public sealed class SqlServerReportingDbContextFactory :
        EFQueryLens.Core.IQueryLensDbContextFactory<SqlServerReportingDbContext>
    {
        public SqlServerReportingDbContext CreateOfflineContext()
        {
            return new SqlServerReportingDbContext(CreateSqlServerOptions<SqlServerReportingDbContext>());
        }

        private static DbContextOptions<TContext> CreateSqlServerOptions<TContext>()
            where TContext : DbContext
        {
            var connectionString = "Server=ef_querylens_offline;Database=ef_querylens_offline;User Id=ef_querylens_offline;Password=ef_querylens_offline;TrustServerCertificate=True";
            return new DbContextOptionsBuilder<TContext>()
                .UseSqlServer(connectionString,
                    sqlServer => sqlServer.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables()
                .Options;
        }
    }
}
