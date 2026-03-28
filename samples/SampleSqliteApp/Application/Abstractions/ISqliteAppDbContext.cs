using SampleSqliteApp.Domain.Entities;

namespace SampleSqliteApp.Application.Abstractions;

public interface ISqliteAppDbContext
{
    IQueryable<Customer> Customers { get; }
    IQueryable<Order> Orders { get; }
    IQueryable<Tag> Tags { get; }
    IQueryable<CustomerTag> CustomerTags { get; }
}
