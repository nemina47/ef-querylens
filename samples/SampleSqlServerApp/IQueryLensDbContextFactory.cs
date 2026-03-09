// ReSharper disable once CheckNamespace
namespace EFQueryLens.Core;

public interface IQueryLensDbContextFactory<out TContext>
    where TContext : Microsoft.EntityFrameworkCore.DbContext
{
    TContext CreateOfflineContext();
}
