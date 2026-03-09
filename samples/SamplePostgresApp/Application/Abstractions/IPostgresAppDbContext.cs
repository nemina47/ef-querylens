using SamplePostgresApp.Domain.Entities;

namespace SamplePostgresApp.Application.Abstractions;

public interface IPostgresAppDbContext
{
    IQueryable<Customer> Customers { get; }
    IQueryable<Order> Orders { get; }
}
