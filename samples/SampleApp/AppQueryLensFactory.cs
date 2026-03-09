using Microsoft.EntityFrameworkCore;
using EFQueryLens.Core;

namespace SampleApp;

/// <summary>
/// QueryLens-native offline factory for <see cref="AppDbContext"/>.
///
/// <para>
/// QueryLens prefers this (<c>IQueryLensDbContextFactory&lt;T&gt;</c>) over
/// <see cref="AppDbContextFactory"/> (<c>IDesignTimeDbContextFactory&lt;T&gt;</c>).
/// <c>dotnet ef migrations</c> continues to use <see cref="AppDbContextFactory"/>
/// — both factories coexist without conflict.
/// </para>
/// </summary>
public class AppQueryLensFactory : IQueryLensDbContextFactory<AppDbContext>
{
    public AppDbContext CreateOfflineContext()
    {
        // Pass a pre-created, closed MySqlConnection instead of a connection string.
        // Pomelo holds a reference to the object without calling Open().
        // ToQueryString() only needs the ServerVersion for SQL dialect - no connection needed.
        var connection = new MySqlConnector.MySqlConnection("Server=__offline__;Database=__querylens__");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connection, new MySqlServerVersion(new Version(8, 0, 0)))
            .Options;
        return new AppDbContext(options);
    }
}
