using SampleMySqlApp.Domain.Entities;

namespace SampleMySqlApp.Application.Abstractions;

public interface IMySqlAppDbContext
{
    IQueryable<Customer> Customers { get; }
    IQueryable<Order> Orders { get; }
    IQueryable<User> Users { get; }
    IQueryable<OrderItem> OrderItems { get; }
    IQueryable<Product> Products { get; }
    IQueryable<Category> Categories { get; }
    IQueryable<ApplicationChecklist> ApplicationChecklists { get; }
    IQueryable<ApplicationChecklistChangeType> ApplicationChecklistChangeTypes { get; }
}
