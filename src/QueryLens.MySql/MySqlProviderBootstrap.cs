using Microsoft.EntityFrameworkCore;
using QueryLens.Core;

namespace QueryLens.MySql;

/// <summary>
/// Configures DbContextOptions for offline SQL generation using the Pomelo MySQL provider.
/// No real database connection is created — ToQueryString() works without one.
/// </summary>
public sealed class MySqlProviderBootstrap : IProviderBootstrap
{
    public string ProviderName => "Pomelo.EntityFrameworkCore.MySql";

    public DbContextOptions ConfigureOffline(Type dbContextType)
    {
        // Pomelo requires a ServerVersion hint even for offline usage.
        // Default to MySQL 8.0 — the minimum version this tool targets.
        var serverVersion = new MySqlServerVersion(new Version(8, 0, 0));

        var optionsBuilder = new DbContextOptionsBuilder();
        optionsBuilder.UseMySql("Server=localhost;Database=__querylens_offline__", serverVersion);

        return optionsBuilder.Options;
    }
}
