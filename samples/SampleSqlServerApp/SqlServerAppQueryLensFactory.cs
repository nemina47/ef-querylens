using Microsoft.EntityFrameworkCore;
using EFQueryLens.Core;

namespace SampleSqlServerApp;

public class SqlServerAppQueryLensFactory : IQueryLensDbContextFactory<SqlServerAppDbContext>
{
    public SqlServerAppDbContext CreateOfflineContext()
    {
        // SQL preview only needs provider metadata and model shape; no live DB call is made.
        var options = new DbContextOptionsBuilder<SqlServerAppDbContext>()
            .UseSqlServer("Server=127.0.0.1,1433;Database=__querylens__;User Id=sa;Password=QueryLens123!;TrustServerCertificate=True")
            .Options;

        return new SqlServerAppDbContext(options);
    }
}
