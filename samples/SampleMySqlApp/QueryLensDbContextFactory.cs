using EntityFrameworkCore.Projectables;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleMySqlApp.Infrastructure.Persistence
{
    public sealed class MySqlReportingDbContextFactory :
        IDesignTimeDbContextFactory<MySqlReportingDbContext>
    {
        public MySqlReportingDbContext CreateDbContext(string[] args)
        {
            return new MySqlReportingDbContext(CreateMySqlOptions<MySqlReportingDbContext>());
        }

        private static DbContextOptions<TContext> CreateMySqlOptions<TContext>()
            where TContext : DbContext
        {
            // Query preview only needs provider metadata/model; no live DB call is made.
            var connectionString = "Server=ef_querylens_offline;Database=ef_querylens_offline;User Id=ef_querylens_offline;Password=ef_querylens_offline";

            var options = new DbContextOptionsBuilder<TContext>()
                .UseMySql(
                    connectionString,
                    new MySqlServerVersion(new Version(8, 0, 36)),
                    mySql => mySql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables()
                .Options;

            return options;
        }
    }

    public sealed class MySqlAppDbContextFactory :
        IDesignTimeDbContextFactory<MySqlAppDbContext>
    {
        public MySqlAppDbContext CreateDbContext(string[] args)
        {
            return new MySqlAppDbContext(CreateMySqlOptions<MySqlAppDbContext>());
        }


        private static DbContextOptions<TContext> CreateMySqlOptions<TContext>()
            where TContext : DbContext
        {
            // Query preview only needs provider metadata/model; no live DB call is made.
            var connectionString = "Server=ef_querylens_offline;Database=ef_querylens_offline;User Id=ef_querylens_offline;Password=ef_querylens_offline";

            var options = new DbContextOptionsBuilder<TContext>()
                .UseMySql(
                    connectionString,
                    new MySqlServerVersion(new Version(8, 0, 36)),
                    mySql => mySql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables()
                .Options;

            return options;
        }
    }

}
