using EntityFrameworkCore.Projectables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SampleSqlServerApp.Application.Abstractions;
using SampleSqlServerApp.Infrastructure.Persistence;

namespace SampleSqlServerApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SampleSqlServer")
            ?? throw new InvalidOperationException("Connection string 'SampleSqlServer' is missing.");

        services.AddDbContext<Persistence.SqlServerAppDbContext>(options =>
            options
                .UseSqlServer(
                    connectionString,
                    sqlServer => sqlServer.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .UseProjectables());

        services.AddScoped<ISqlServerAppDbContext>(sp => sp.GetRequiredService<Persistence.SqlServerAppDbContext>());
        return services;
    }
}
