using EFQueryLens.Core;
using Microsoft.EntityFrameworkCore;

namespace SampleSqlServerApp.Infrastructure.Persistence;

public class SqlServerAppQueryLensFactory : IQueryLensDbContextFactory<SqlServerAppDbContext>
{
    public SqlServerAppDbContext CreateOfflineContext()
    {
        // SQL preview only needs provider metadata/model; no live DB call is made.
        var options = new DbContextOptionsBuilder<SqlServerAppDbContext>()
            .UseSqlServer("Server=127.0.0.1,1433;Database=__querylens__;User Id=sa;Password=QueryLens123!;TrustServerCertificate=True")
            .Options;

        return new SqlServerAppDbContext(options);
    }
}
