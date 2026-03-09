using EFQueryLens.Core;
using Microsoft.EntityFrameworkCore;

namespace SamplePostgresApp.Infrastructure.Persistence;

public sealed class PostgresAppQueryLensFactory : IQueryLensDbContextFactory<PostgresAppDbContext>
{
    public PostgresAppDbContext CreateOfflineContext()
    {
        // Query preview only needs provider metadata/model; no live DB call is made.
        var options = new DbContextOptionsBuilder<PostgresAppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=5432;Database=__querylens__;Username=postgres;Password=__querylens__")
            .Options;

        return new PostgresAppDbContext(options);
    }
}
