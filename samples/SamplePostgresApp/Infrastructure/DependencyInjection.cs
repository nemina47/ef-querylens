using EntityFrameworkCore.Projectables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SamplePostgresApp.Application.Abstractions;
using SamplePostgresApp.Infrastructure.Persistence;

namespace SamplePostgresApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SamplePostgres")
            ?? throw new InvalidOperationException("Connection string 'SamplePostgres' is missing.");

        services.AddDbContext<PostgresAppDbContext>(options =>
            options
                .UseNpgsql(
                    connectionString,
                    npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables());

        services.AddScoped<IPostgresAppDbContext>(sp => sp.GetRequiredService<PostgresAppDbContext>());
        return services;
    }
}
