using SampleSqlServerApp.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace SampleSqlServerApp.Application.Orders;

public sealed class OrderQueries
{
    private readonly ISqlServerAppDbContext _db;

    public OrderQueries(ISqlServerAppDbContext db)
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

    public async Task<IReadOnlyList<OrderSummaryDto>> BuildRecentOrdersQueryExpressionAsync(
        DateTime utcNow,
        int lookbackDays = 30,
        CancellationToken ct = default)
    {
        var safeLookbackDays = Math.Clamp(lookbackDays, 1, 365);
        var fromUtc = utcNow.Date.AddDays(-safeLookbackDays);

        return await (from o in _db.Orders
            where o.IsNotDeleted && o.CreatedUtc >= fromUtc
            orderby o.CreatedUtc descending
            select new OrderSummaryDto(
                o.Id,
                o.Customer.Name,
                o.Total,
                o.CreatedUtc)).ToListAsync(ct);
    }
}
