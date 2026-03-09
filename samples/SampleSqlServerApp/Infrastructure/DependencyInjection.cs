using Microsoft.Extensions.DependencyInjection;
using SampleSqlServerApp.Application.Abstractions;
using SampleSqlServerApp.Infrastructure.Persistence;

namespace SampleSqlServerApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<Persistence.SqlServerAppDbContext>(_ => new Persistence.SqlServerAppQueryLensFactory().CreateOfflineContext());
        services.AddScoped<ISqlServerAppDbContext>(sp => sp.GetRequiredService<Persistence.SqlServerAppDbContext>());
        return services;
    }
}
