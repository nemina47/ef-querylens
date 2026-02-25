using Microsoft.EntityFrameworkCore;
using QueryLens.Core;

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
    public AppDbContext CreateOfflineContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(
                "Server=localhost;Database=__querylens_offline__",
                new MySqlServerVersion(new Version(8, 0, 0)))
            .Options);
}
