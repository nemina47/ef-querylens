using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SampleSqliteApp.Application.Abstractions;
using SampleSqliteApp.Application.Orders;
using SampleSqliteApp.Domain.Entities;
using SampleSqliteApp.Domain.Enums;

namespace SampleSqliteApp.Application.Customers;

public sealed class CustomerReadService
{
    private readonly ISqliteAppDbContext _db;

    public CustomerReadService(ISqliteAppDbContext db)
    {
        _db = db;
    }

    // ── Simple projection ────────────────────────────────────────────────────

    /// <summary>
    /// Generic selector — hover on a call site in Program.cs to see the
    /// full SQL with your projection substituted in.
    /// </summary>
    public IQueryable<TResult> GetActiveCustomersQuery<TResult>(
        Expression<Func<Customer, TResult>> selector)
    {
        return _db.Customers
            .Where(c => !c.IsDeleted && c.IsActive)
            .OrderBy(c => c.Name)
            .Select(selector);
    }

    // ── Search with optional filters ─────────────────────────────────────────

    /// <summary>
    /// Builds a filtered customer query.  Hover to see the full SQL for whatever
    /// filters are active; inactive optional clauses are omitted from the WHERE.
    /// </summary>
    public IQueryable<TResult> SearchCustomersQuery<TResult>(
        CustomerSearchRequest request,
        Expression<Func<Customer, TResult>> selector)
    {
        var query = _db.Customers.Where(c => !c.IsDeleted);

        if (!string.IsNullOrWhiteSpace(request.NameContains))
        {
            var term = request.NameContains.Trim().ToLower();
            query = query.Where(c => c.Name.ToLower().Contains(term));
        }

        if (request.IsActive.HasValue)
        {
            var active = request.IsActive.Value;
            query = query.Where(c => c.IsActive == active);
        }

        if (request.CreatedAfterUtc.HasValue)
        {
            var from = request.CreatedAfterUtc.Value;
            query = query.Where(c => c.CreatedUtc >= from);
        }

        if (request.TagName is not null)
        {
            var tag = request.TagName;
            query = query.Where(c => c.CustomerTags.Any(ct => ct.Tag.Name == tag));
        }

        return query
            .OrderByDescending(c => c.CreatedUtc)
            .Select(selector);
    }

    // ── Order queries ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns orders for a specific customer with optional status filter.
    /// Hover to see the parameterised SQL EF generates.
    /// </summary>
    public IQueryable<OrderSummaryDto> GetCustomerOrdersQuery(
        Guid customerId,
        OrderStatus? status = null)
    {
        var query = _db.Orders
            .Where(o => !o.IsDeleted && o.Customer.CustomerId == customerId);

        if (status.HasValue)
        {
            var s = status.Value;
            query = query.Where(o => o.Status == s);
        }

        return query
            .OrderByDescending(o => o.CreatedUtc)
            .Select(o => new OrderSummaryDto(
                o.Id,
                o.Customer.Name,
                o.Total,
                o.Status,
                o.CreatedUtc));
    }

    public async Task<IReadOnlyList<OrderSummaryDto>> GetCustomerOrdersQueryExpressionAsync(
        Guid customerId,
        OrderStatus? status = null,
        CancellationToken ct = default)
    {
        return await (from o in _db.Orders
            where !o.IsDeleted && o.Customer.CustomerId == customerId
            where !status.HasValue || o.Status == status.Value
            orderby o.CreatedUtc descending
            select new OrderSummaryDto(
                o.Id,
                o.Customer.Name,
                o.Total,
                o.Status,
                o.CreatedUtc)).ToListAsync(ct);
    }

    // ── Aggregation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Revenue roll-up per customer — demonstrates GROUP BY translation.
    /// Hover to see the aggregation SQL.
    /// </summary>
    public IQueryable<CustomerRevenueDto> GetRevenueByCustomerQuery(DateTime fromUtc)
    {
        return _db.Orders
            .Where(o => !o.IsDeleted && o.CreatedUtc >= fromUtc)
            .GroupBy(o => new { o.Customer.CustomerId, o.Customer.Name })
            .Select(g => new CustomerRevenueDto(
                g.Key.CustomerId,
                g.Key.Name,
                g.Count(),
                g.Sum(o => o.Total),
                g.Average(o => o.Total)));
    }

    // ── Many-to-many ─────────────────────────────────────────────────────────

    /// <summary>
    /// Customers that have ALL of the supplied tags (set intersection via subquery).
    /// Hover to see the correlated COUNT subquery EF generates.
    /// </summary>
    public IQueryable<string> GetCustomerNamesWithAllTagsQuery(IReadOnlyList<string> tagNames)
    {
        var count = tagNames.Count;
        return _db.Customers
            .Where(c => !c.IsDeleted
                && c.CustomerTags.Count(ct => tagNames.Contains(ct.Tag.Name)) == count)
            .Select(c => c.Name);
    }

    // ── Include / split query ────────────────────────────────────────────────

    /// <summary>
    /// Eager-loads orders for recent customers.
    /// Hover to see the split-query SQL (two SELECT statements).
    /// </summary>
    public IQueryable<Customer> GetRecentCustomersWithOrdersQuery(DateTime fromUtc)
    {
        return _db.Customers
            .Where(c => !c.IsDeleted && c.CreatedUtc >= fromUtc)
            .Include(c => c.Orders.Where(o => !o.IsDeleted))
            .OrderByDescending(c => c.CreatedUtc)
            .Take(20)
            .AsSplitQuery();
    }

    public sealed class CustomerSearchRequest
    {
        public string? NameContains { get; init; }
        public bool? IsActive { get; init; }
        public DateTime? CreatedAfterUtc { get; init; }
        public string? TagName { get; init; }
    }

    public sealed record CustomerRevenueDto(
        Guid CustomerId,
        string CustomerName,
        int OrderCount,
        decimal Revenue,
        decimal AverageOrderValue);
}
