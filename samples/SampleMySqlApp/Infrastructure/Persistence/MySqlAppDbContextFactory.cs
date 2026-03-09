using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleMySqlApp.Infrastructure.Persistence;

public sealed class MySqlAppDbContextFactory : IDesignTimeDbContextFactory<MySqlAppDbContext>
{
    public MySqlAppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SAMPLE_MYSQL_CONNECTION_STRING")
                               ?? "Server=127.0.0.1;Port=3306;Database=sample_mysql_app;User=root;Password=sample_password";

        var options = new DbContextOptionsBuilder<MySqlAppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;

        return new MySqlAppDbContext(options);
    }
}
