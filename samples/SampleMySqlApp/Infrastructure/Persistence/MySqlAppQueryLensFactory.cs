using EFQueryLens.Core;
using Microsoft.EntityFrameworkCore;

namespace SampleMySqlApp.Infrastructure.Persistence;

public sealed class MySqlAppQueryLensFactory : IQueryLensDbContextFactory<MySqlAppDbContext>
{
    public MySqlAppDbContext CreateOfflineContext()
    {
        // Query preview only needs provider metadata/model; no live DB call is made.
        var options = new DbContextOptionsBuilder<MySqlAppDbContext>()
            .UseMySql(
                "Server=127.0.0.1;Port=3306;Database=__querylens__;User=root;Password=__querylens__",
                new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;

        return new MySqlAppDbContext(options);
    }
}
