using SampleSqlServerApp.Domain.Entities;

namespace SampleSqlServerApp.Application.Abstractions;

public interface ISqlServerAppDbContext
{
    IQueryable<Customer> Customers { get; }
    IQueryable<Order> Orders { get; }
}
