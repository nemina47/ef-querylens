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

namespace SampleSqliteApp.Infrastructure.Persistence
{
    /// <summary>
    /// Tells EF QueryLens how to create a DbContext without a real database connection.
    /// SQLite uses an in-memory database for offline query translation — no connection
    /// string required, no server running.
    /// </summary>
    public sealed class SqliteAppDbContextFactory :
        EFQueryLens.Core.IQueryLensDbContextFactory<SqliteAppDbContext>
    {
        public SqliteAppDbContext CreateOfflineContext()
        {
            var options = new DbContextOptionsBuilder<SqliteAppDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options;

            return new SqliteAppDbContext(options);
        }
    }
}
