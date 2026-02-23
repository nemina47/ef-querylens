using Microsoft.EntityFrameworkCore;

namespace QueryLens.Core;

/// <summary>
/// Configures DbContextOptions with a fake/offline connection string so that
/// ToQueryString() works without a real database connection.
/// Each provider package (MySql, Postgres, SqlServer) supplies one implementation.
/// </summary>
public interface IProviderBootstrap
{
    string ProviderName { get; }

    DbContextOptions ConfigureOffline(Type dbContextType);
}
