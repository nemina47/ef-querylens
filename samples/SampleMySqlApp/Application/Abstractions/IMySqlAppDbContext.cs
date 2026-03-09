using SampleMySqlApp.Domain.Entities;

namespace SampleMySqlApp.Application.Abstractions;

public interface IMySqlAppDbContext
{
    IQueryable<Customer> Customers { get; }
    IQueryable<Order> Orders { get; }
}
