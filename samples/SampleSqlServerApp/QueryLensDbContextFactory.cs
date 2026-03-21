using EntityFrameworkCore.Projectables;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleSqlServerApp.Infrastructure.Persistence
{
    public sealed class SqlServerAppDbContextFactory :
        IDesignTimeDbContextFactory<SqlServerAppDbContext>
    {
        public SqlServerAppDbContext CreateDbContext(string[] args)
        {
            return new SqlServerAppDbContext(CreateSqlServerOptions<SqlServerAppDbContext>());
        }

        private static DbContextOptions<TContext> CreateSqlServerOptions<TContext>()
            where TContext : DbContext
        {
            // SQL preview only needs provider metadata/model; no live DB call is made.
            var connectionString = "Server=ef_querylens_offline;Database=ef_querylens_offline;User Id=ef_querylens_offline;Password=ef_querylens_offline;TrustServerCertificate=True";

            var options = new DbContextOptionsBuilder<TContext>()
                .UseSqlServer(
                    connectionString,
                    sqlServer => sqlServer.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables()
                .Options;

            return options;
        }
    }

    public sealed class SqlServerReportingDbContextFactory :
        IDesignTimeDbContextFactory<SqlServerReportingDbContext>
    {
        public SqlServerReportingDbContext CreateDbContext(string[] args)
        {
            return new SqlServerReportingDbContext(CreateSqlServerOptions<SqlServerReportingDbContext>());
        }

        private static DbContextOptions<TContext> CreateSqlServerOptions<TContext>()
            where TContext : DbContext
        {
            // SQL preview only needs provider metadata/model; no live DB call is made.
            var connectionString = "Server=ef_querylens_offline;Database=ef_querylens_offline;User Id=ef_querylens_offline;Password=ef_querylens_offline;TrustServerCertificate=True";

            var options = new DbContextOptionsBuilder<TContext>()
                .UseSqlServer(
                    connectionString,
                    sqlServer => sqlServer.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables()
                .Options;

            return options;
        }
    }
}
