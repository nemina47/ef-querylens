using Microsoft.EntityFrameworkCore;
using SampleSqliteApp.Application;
using SampleSqliteApp.Application.Customers;
using SampleSqliteApp.Domain.Enums;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

var sampleCustomerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

app.MapGet("/", () => Results.Ok(new { Message = "SampleSqliteApp is running." }));

// ── Hover these endpoints to preview SQL ─────────────────────────────────────

// Hover on the lambda below — EF QueryLens will find GetActiveCustomersQuery in
// CustomerReadService.cs, substitute your projection, and show the full SQL.
app.MapGet("/api/customers", async (CustomerReadService svc, CancellationToken ct) =>
{
    var customers = await svc.GetActiveCustomersQuery(c => new
    {
        c.CustomerId,
        c.Name,
        c.Email,
        OrderCount = c.Orders.Count(o => !o.IsDeleted),
    }).ToListAsync(ct);

    return Results.Ok(customers);
});

app.MapGet("/api/customers/search", async (
    string? name,
    bool? isActive,
    string? tag,
    CustomerReadService svc,
    CancellationToken ct) =>
{
    var results = await svc.SearchCustomersQuery(
        new CustomerReadService.CustomerSearchRequest
        {
            NameContains = name,
            IsActive = isActive,
            TagName = tag,
        },
        c => new { c.CustomerId, c.Name, c.Email, c.IsActive })
        .ToListAsync(ct);

    return Results.Ok(results);
});

app.MapGet("/api/customers/{customerId:guid}/orders", async (
    Guid customerId,
    OrderStatus? status,
    CustomerReadService svc,
    CancellationToken ct) =>
{
    var orders = await svc.GetCustomerOrdersQuery(customerId, status).ToListAsync(ct);
    return Results.Ok(orders);
});

app.MapGet("/api/revenue", async (CustomerReadService svc, CancellationToken ct) =>
{
    var revenue = await svc
        .GetRevenueByCustomerQuery(DateTime.UtcNow.AddDays(-30))
        .ToListAsync(ct);
    return Results.Ok(revenue);
});

app.Run();
