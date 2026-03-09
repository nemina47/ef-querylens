using Microsoft.Extensions.DependencyInjection;
using SampleMySqlApp.Application.Customers;
using SampleMySqlApp.Application.Orders;

namespace SampleMySqlApp.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<OrderQueries>();
        services.AddScoped<CustomerReadService>();
        return services;
    }
}
