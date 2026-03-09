using Microsoft.Extensions.DependencyInjection;
using SampleMySqlApp.Application.Abstractions;
using SampleMySqlApp.Infrastructure.Persistence;

namespace SampleMySqlApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<MySqlAppDbContext>(_ => new MySqlAppQueryLensFactory().CreateOfflineContext());
        services.AddScoped<IMySqlAppDbContext>(sp => sp.GetRequiredService<MySqlAppDbContext>());
        return services;
    }
}
