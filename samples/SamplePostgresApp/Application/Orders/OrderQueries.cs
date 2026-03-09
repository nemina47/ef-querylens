using SamplePostgresApp.Application.Abstractions;

namespace SamplePostgresApp.Application.Orders;

public sealed class OrderQueries
{
    private readonly IPostgresAppDbContext _db;

    public OrderQueries(IPostgresAppDbContext db)
    {
        _db = db;
    }

    public IQueryable<OrderSummaryDto> BuildRecentOrdersQuery(DateTime utcNow)
    {
        var fromUtc = utcNow.Date.AddDays(-30);

        return _db.Orders
            .Where(o => o.CreatedUtc >= fromUtc)
            .OrderByDescending(o => o.CreatedUtc)
            .Select(o => new OrderSummaryDto(
                o.Id,
                o.Customer.Name,
                o.Total,
                o.CreatedUtc));
    }
}
