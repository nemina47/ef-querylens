using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SamplePostgresApp.Infrastructure.Persistence;

public sealed class PostgresAppDbContextFactory : IDesignTimeDbContextFactory<PostgresAppDbContext>
{
    public PostgresAppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SAMPLE_POSTGRES_CONNECTION_STRING")
                               ?? "Host=127.0.0.1;Port=5432;Database=sample_postgres_app;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<PostgresAppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new PostgresAppDbContext(options);
    }
}
