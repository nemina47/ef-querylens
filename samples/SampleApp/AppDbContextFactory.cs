using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleApp;

/// <summary>
/// Design-time factory for <see cref="AppDbContext"/>.
///
/// EF Core tooling (<c>dotnet ef migrations</c>) and QueryLens both discover
/// this factory automatically. It uses a fake offline connection string —
/// no real database is ever contacted during SQL preview.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(
                "Server=localhost;Database=__querylens_offline__",
                new MySqlServerVersion(new Version(8, 0, 0)))
            .Options;

        return new AppDbContext(options);
    }
}
