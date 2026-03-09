using Microsoft.Extensions.DependencyInjection;
using SamplePostgresApp.Application.Abstractions;
using SamplePostgresApp.Infrastructure.Persistence;

namespace SamplePostgresApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<PostgresAppDbContext>(_ => new PostgresAppQueryLensFactory().CreateOfflineContext());
        services.AddScoped<IPostgresAppDbContext>(sp => sp.GetRequiredService<PostgresAppDbContext>());
        return services;
    }
}
