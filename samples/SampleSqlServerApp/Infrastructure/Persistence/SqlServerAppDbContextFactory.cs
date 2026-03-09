using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleSqlServerApp.Infrastructure.Persistence;

public class SqlServerAppDbContextFactory : IDesignTimeDbContextFactory<SqlServerAppDbContext>
{
    public SqlServerAppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SAMPLE_SQLSERVER_CONNECTION_STRING")
                               ?? "Server=127.0.0.1,1433;Database=sample_sqlserver_app;User Id=sa;Password=QueryLens123!;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<SqlServerAppDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new SqlServerAppDbContext(options);
    }
}
