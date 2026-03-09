using Microsoft.Extensions.DependencyInjection;
using SamplePostgresApp.Application.Customers;
using SamplePostgresApp.Application.Orders;

namespace SamplePostgresApp.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<OrderQueries>();
        services.AddScoped<CustomerReadService>();
        return services;
    }
}
