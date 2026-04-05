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
        services.AddDbContext<SqlServerAppDbContext>();

        services.AddScoped<ISqlServerAppDbContext>(sp => sp.GetRequiredService<SqlServerAppDbContext>());

        services.AddDbContext<SqlServerReportingDbContext>();

        return services;
    }
}
