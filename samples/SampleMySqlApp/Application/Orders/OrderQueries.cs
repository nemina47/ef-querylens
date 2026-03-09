using SampleMySqlApp.Application.Abstractions;

namespace SampleMySqlApp.Application.Orders;

public sealed class OrderQueries
{
    private readonly IMySqlAppDbContext _db;

    public OrderQueries(IMySqlAppDbContext db)
    {
        _db = db;
    }

    public IQueryable<OrderSummaryDto> BuildRecentOrdersQuery(DateTime utcNow, int lookbackDays = 30)
    {
        var safeLookbackDays = Math.Clamp(lookbackDays, 1, 365);
        var fromUtc = utcNow.Date.AddDays(-safeLookbackDays);

        return _db.Orders
            .Where(o => o.IsNotDeleted)
            .Where(o => o.CreatedUtc >= fromUtc)
            .OrderByDescending(o => o.CreatedUtc)
            .Select(o => new OrderSummaryDto(
                o.Id,
                o.Customer.Name,
                o.Total,
                o.CreatedUtc));
    }
}
