using Microsoft.EntityFrameworkCore;
using SamplePostgresApp.Application.Orders;
using SamplePostgresApp.Domain.Enums;
using SamplePostgresApp.Infrastructure.Persistence;

namespace SamplePostgresApp.QueryScenarios;

/// <summary>
/// Demonstrates how EF Core global query filters interact with EF QueryLens hover previews.
///
/// The PostgresAppDbContext defines two filters:
///   - Customer: WHERE NOT is_deleted AND is_active
///   - Order:    WHERE NOT is_deleted
///
/// Hover over any query below to see exactly how the filter is folded into the SQL.
/// </summary>
public sealed class QueryFilterSamples
{
    private readonly PostgresAppDbContext _db;

    public QueryFilterSamples(PostgresAppDbContext db)
    {
        _db = db;
    }

    // ── Filter applied (default) ─────────────────────────────────────────────

    /// <summary>
    /// Global filter is applied automatically.
    /// Hover to see WHERE NOT is_deleted AND is_active in the generated SQL.
    /// </summary>
    public IQueryable<string> ActiveCustomerNames()
    {
        return _db.Customers
            .OrderBy(c => c.Name)
            .Select(c => c.Name);
    }

    /// <summary>
    /// Both the Customer and Order filters apply when navigating the relationship.
    /// Hover to see both WHERE NOT is_deleted clauses in the JOIN.
    /// </summary>
    public IQueryable<OrderSummaryDto> RecentOrdersWithFilters(DateTime utcNow)
    {
        return _db.Orders
            .Where(o => o.CreatedUtc >= utcNow.AddDays(-30))
            .OrderByDescending(o => o.CreatedUtc)
            .Select(o => new OrderSummaryDto(
                o.Id,
                o.Customer.Name,
                o.Total,
                o.CreatedUtc));
    }

    // ── Filter bypassed ──────────────────────────────────────────────────────

    /// <summary>
    /// IgnoreQueryFilters() bypasses ALL global filters on this query.
    /// Hover to see the SQL without any is_deleted / is_active predicates —
    /// useful for admin views, audit logs, or restore operations.
    /// </summary>
    public IQueryable<string> AllCustomerNamesIncludingDeleted()
    {
        return _db.Customers
            .IgnoreQueryFilters()
            .OrderBy(c => c.Name)
            .Select(c => c.Name);
    }

    /// <summary>
    /// Bypassing filters on Orders also removes the Customer soft-delete filter
    /// because IgnoreQueryFilters() is query-wide, not per-entity.
    /// Hover to confirm no filter predicates appear anywhere in the SQL.
    /// </summary>
    public IQueryable<OrderSummaryDto> AllOrdersIgnoringFilters()
    {
        return _db.Orders
            .IgnoreQueryFilters()
            .OrderByDescending(o => o.CreatedUtc)
            .Select(o => new OrderSummaryDto(
                o.Id,
                o.Customer.Name,
                o.Total,
                o.CreatedUtc));
    }

    // ── Filter + explicit predicate ──────────────────────────────────────────

    /// <summary>
    /// The global filter and your own Where() combine with AND.
    /// Hover to see: WHERE NOT is_deleted AND is_active AND status = 'Pending'
    /// </summary>
    public IQueryable<string> PendingActiveCustomerEmails()
    {
        return _db.Orders
            .Where(o => o.Status == OrderStatus.Pending)
            .Select(o => o.Customer.Email)
            .Distinct();
    }

    // ── Counting through filters ─────────────────────────────────────────────

    /// <summary>
    /// Scalar aggregates respect global filters too.
    /// Hover to see COUNT(*) scoped to non-deleted active customers.
    /// </summary>
    public IQueryable<int> ActiveCustomerCount()
    {
        // Wrap in IQueryable so EF QueryLens can preview without executing.
        return _db.Customers
            .Select(_ => 1);
        // In real code you'd call .CountAsync() here.
    }

    /// <summary>
    /// Same count without filters — useful for a "total including deleted" stat.
    /// Hover to compare the SQL against ActiveCustomerCount().
    /// </summary>
    public IQueryable<int> TotalCustomerCountIgnoringFilters()
    {
        return _db.Customers
            .IgnoreQueryFilters()
            .Select(_ => 1);
    }
}
