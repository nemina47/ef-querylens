using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SampleSqliteApp.Application.Abstractions;
using SampleSqliteApp.Application.Customers;
using SampleSqliteApp.Infrastructure.Persistence;

namespace SampleSqliteApp.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services) =>
        services.AddScoped<CustomerReadService>();

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Sqlite")
            ?? "Data Source=sample.db";

        services.AddDbContext<SqliteAppDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped<ISqliteAppDbContext>(sp =>
            sp.GetRequiredService<SqliteAppDbContext>());

        return services;
    }
}
