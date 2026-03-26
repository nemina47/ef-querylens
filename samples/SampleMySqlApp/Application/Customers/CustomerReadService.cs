using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SampleMySqlApp.Application.Abstractions;
using SampleMySqlApp.Domain.Entities;
using SampleMySqlApp.Domain.Enums;

namespace SampleMySqlApp.Application.Customers;

public sealed class CustomerReadService
{
    private readonly IMySqlAppDbContext _dbContext;

    public CustomerReadService(IMySqlAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<TResult?> GetCustomerByIdAsync<TResult>(
        Guid customerId,
        Expression<Func<Customer, TResult>> expression,
        CancellationToken ct)
    {
        return await _dbContext
            .Customers.Include(c => c.Orders) // Include orders for potential use in the expression
            .Where(c => c.IsNotDeleted)
            .Where(c => c.CustomerId == customerId)
            .Select(expression)
            .SingleOrDefaultAsync(ct);
    }

    public IQueryable<TResult> GetCustomerByIdQuery<TResult>(
        Guid customerId,
        Expression<Func<Customer, TResult>> expression)
    {
        return _dbContext
            .Customers
            .Where(c => c.IsNotDeleted)
            .Where(c => c.CustomerId == customerId)
            .Select(expression);
    }

    public async Task<IReadOnlyList<TResult>> GetCustomersAsync<TResult>(
        CustomerQueryRequest request,
        Expression<Func<Customer, TResult>> expression,
        CancellationToken ct)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);


        _ = page > 1 ?
            (await _dbContext.Customers.FirstAsync())
            : (await  _dbContext.Customers.Where(w => w.IsActive).FirstAsync());


    var query = _dbContext.Customers
            .Where(c => c.IsNotDeleted)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var term = request.SearchTerm.Trim().ToLowerInvariant();
            query = query.Where(c =>
                c.Name.ToLower().Contains(term)
                || c.Email.ToLower().Contains(term)
                || c.Email.ToLower().StartsWith(term));
        }

        if (request.IsActive is not null)
        {
            var isActive = request.IsActive.Value;
            query = query.Where(c => c.IsActive == isActive);
        }

        if (request.CreatedAfterUtc is not null)
        {
            var createdAfter = request.CreatedAfterUtc.Value;
            query = query.Where(c => c.CreatedUtc >= createdAfter);
        }

        if (request.MinOrders is not null)
        {
            var minOrders = request.MinOrders.Value;
            query = query.Where(c => c.Orders.Count(o => o.IsNotDeleted) >= minOrders);
        }

        return await query
            .OrderByDescending(c => c.CreatedUtc)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(expression)
            .ToListAsync(ct);
    }

    public IQueryable<TResult> GetCustomersQuery<TResult>(
        CustomerQueryRequest request,
        Expression<Func<Customer, TResult>> expression)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);

        var query = _dbContext.Customers
            .Where(c => c.IsNotDeleted)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var term = request.SearchTerm.Trim().ToLowerInvariant();
            query = query.Where(c =>
                c.Name.ToLower().Contains(term)
                || c.Email.ToLower().Contains(term)
                || c.Email.ToLower().StartsWith(term));
        }

        if (request.IsActive is not null)
        {
            var isActive = request.IsActive.Value;
            query = query.Where(c => c.IsActive == isActive);
        }

        if (request.CreatedAfterUtc is not null)
        {
            var createdAfter = request.CreatedAfterUtc.Value;
            query = query.Where(c => c.CreatedUtc >= createdAfter);
        }

        if (request.MinOrders is not null)
        {
            var minOrders = request.MinOrders.Value;
            query = query.Where(c => c.Orders.Count(o => o.IsNotDeleted) >= minOrders);
        }

        return query
            .OrderByDescending(c => c.CreatedUtc)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(expression);
    }

    public async Task<IReadOnlyList<TResult>> GetCustomerOrdersAsync<TResult>(
        Guid customerId,
        Expression<Func<Order, bool>> whereExpression,
        Expression<Func<Order, TResult>> selectExpression,
        CancellationToken ct)
    {
        return await _dbContext
            .Orders
            .Where(o => o.Customer.CustomerId == customerId)
            .Where(o => o.IsNotDeleted)
            .Where(whereExpression)
            .Select(selectExpression)
            .ToListAsync(ct);
    }

    public IQueryable<TResult> GetCustomerOrdersQuery<TResult>(
        Guid customerId,
        Expression<Func<Order, bool>> whereExpression,
        Expression<Func<Order, TResult>> selectExpression)
    {
        return _dbContext
            .Orders
            .Where(o => o.Customer.CustomerId == customerId)
            .Where(o => o.IsNotDeleted)
            .Where(whereExpression)
            .Select(selectExpression);
    }

    public async Task<PagedResult<TResult>> GetPagedOrdersAsync<TResult>(
        OrderQueryRequest request,
        Expression<Func<Order, TResult>> expression,
        CancellationToken ct)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);

        var baseQuery = _dbContext.Orders.Where(o => o.IsNotDeleted);

        if (request.CustomerId is not null)
        {
            var customerId = request.CustomerId.Value;
            baseQuery = baseQuery.Where(o => o.Customer.CustomerId == customerId && o.Status == OrderStatus.Confirmed);
        }
        if (request.Status is not null)
        {
            var status = request.Status.Value;
            baseQuery = baseQuery.Where(o => o.Status == status);
        }

        if (request.MinTotal is not null)
        {
            var minTotal = request.MinTotal.Value;
            baseQuery = baseQuery.Where(o => o.Total >= minTotal);
        }

        if (request.CreatedAfterUtc is not null)
        {
            var fromUtc = request.CreatedAfterUtc.Value;
            baseQuery = baseQuery.Where(o => o.CreatedUtc >= fromUtc);
        }

        if (!string.IsNullOrWhiteSpace(request.NotesSearch))
        {
            var term = request.NotesSearch.Trim().ToLowerInvariant();
            baseQuery = baseQuery.Where(o => o.Notes != null && o.Notes.ToLower().Contains(term));
        }

        var totalCount = await baseQuery.CountAsync(ct);

        var items = await baseQuery
            .OrderByDescending(o => o.CreatedUtc)
            .ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(expression)
            .ToListAsync(ct);

        return new PagedResult<TResult>(items, totalCount, page, pageSize);
    }

    public IQueryable<TResult> GetPagedOrdersQuery<TResult>(
        OrderQueryRequest request,
        Expression<Func<Order, TResult>> expression)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);

        var query = _dbContext.Orders.Where(o => o.IsNotDeleted);

        if (request.CustomerId is not null)
        {
            var customerId = request.CustomerId.Value;
            query = query.Where(o => o.Customer.CustomerId == customerId && o.Status == OrderStatus.Confirmed);
        }

        if (request.Status is not null)
        {
            var status = request.Status.Value;
            query = query.Where(o => o.Status == status);
        }

        if (request.MinTotal is not null)
        {
            var minTotal = request.MinTotal.Value;
            query = query.Where(o => o.Total >= minTotal);
        }

        if (request.CreatedAfterUtc is not null)
        {
            var fromUtc = request.CreatedAfterUtc.Value;
            query = query.Where(o => o.CreatedUtc >= fromUtc);
        }

        if (!string.IsNullOrWhiteSpace(request.NotesSearch))
        {
            var term = request.NotesSearch.Trim().ToLowerInvariant();
            query = query.Where(o => o.Notes != null && o.Notes.ToLower().Contains(term));
        }

        return query
            .OrderByDescending(o => o.CreatedUtc)
            .ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(expression);
    }

    public IReadOnlyList<(string Title, IQueryable Query)> BuildSqlPreviewCatalog(Guid customerId, DateTime utcNow)
    {
        var customerQuery = GetCustomerByIdQuery(
            customerId,
            c => new CustomerDetailsDto(
                c.CustomerId,
                c.Name,
                c.Email,
                c.IsActive));

        var customersSearch = GetCustomersQuery(
            new CustomerQueryRequest
            {
                SearchTerm = "mail",
                IsActive = true,
                CreatedAfterUtc = utcNow.Date.AddYears(-1)
            },
            c => new CustomerListItemDto(c.CustomerId, c.Name, c.Email, c.IsActive));

        var customerOrders = GetCustomerOrdersQuery(
            customerId,
            o => o.Total >= 100 && o.Status != OrderStatus.Cancelled,
            o => new OrderListItemDto(o.Id, o.Customer.CustomerId, o.Total, o.Status, o.CreatedUtc));

        var pagedOrders = GetPagedOrdersQuery(
            new OrderQueryRequest
            {
                CustomerId = customerId,
                Status = OrderStatus.Confirmed,
                MinTotal = 150,
                CreatedAfterUtc = utcNow.Date.AddDays(-30),
                NotesSearch = "priority",
                Page = 2,
                PageSize = 25
            },
            o => new OrderListItemDto(o.Id, o.Customer.CustomerId, o.Total, o.Status, o.CreatedUtc));

        var revenue = GetRevenueByCustomerQuery(utcNow.Date.AddDays(-30));
        var activeHighValueCustomers = GetCustomersWithRecentOrderQuery(utcNow.Date.AddDays(-14), 200);
        var customersWithRecentOrdersSplit = _dbContext.Customers
            .Where(c => c.IsNotDeleted)
            .Include(c => c.Orders.Where(o => o.IsNotDeleted && o.CreatedUtc >= utcNow.Date.AddDays(-30)))
            .OrderByDescending(c => c.CreatedUtc)
            .Take(10);

        var customersWithHighValueOrdersSplit = _dbContext.Customers
            .Where(c => c.IsNotDeleted)
            .Include(c => c.Orders.Where(o => o.IsNotDeleted && o.Total >= 150))
            .OrderBy(c => c.Id)
            .Take(10);

        return
        [
            ("GetCustomerByIdAsync<TResult>", customerQuery),
            ("GetCustomersAsync<TResult> (conditional)", customersSearch),
            ("GetCustomerOrdersAsync<TResult> (expression where/select)", customerOrders),
            ("GetPagedOrdersAsync<TResult>", pagedOrders),
            ("Revenue aggregation", revenue),
            ("Correlated subquery (Any + Average)", activeHighValueCustomers),
            ("Split query trigger (Customers + recent Orders include)", customersWithRecentOrdersSplit),
            ("Split query trigger (Customers + high-value Orders include)", customersWithHighValueOrdersSplit)
        ];
    }

    public IQueryable<CustomerRevenueDto> GetRevenueByCustomerQuery(DateTime fromUtc)
    {
        return _dbContext.Orders
            .Where(o => o.IsNotDeleted && o.CreatedUtc >= fromUtc)
            .GroupBy(o => new { o.Customer.CustomerId, o.Customer.Name })
            .Select(g => new CustomerRevenueDto(
                g.Key.CustomerId,
                g.Key.Name,
                g.Count(),
                g.Sum(o => o.Total),
                g.Average(o => o.Total)));
    }

    public IQueryable<CustomerHealthDto> GetCustomersWithRecentOrderQuery(DateTime fromUtc, decimal minTotal)
    {
        return _dbContext.Customers
            .Where(c => c.IsNotDeleted)
            .Where(c => c.Orders.Any(o => o.IsNotDeleted && o.CreatedUtc >= fromUtc && o.Total >= minTotal))
            .Select(c => new CustomerHealthDto(
                c.CustomerId,
                c.Name,
                c.Orders.Count(o => o.IsNotDeleted),
                c.Orders.Average(o => (decimal?)o.Total) ?? 0));
    }

    public sealed class CustomerQueryRequest
    {
        public string? SearchTerm { get; init; }
        public bool? IsActive { get; init; }
        public DateTime? CreatedAfterUtc { get; init; }
        public int? MinOrders { get; init; }
        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 25;
    }

    public sealed class OrderQueryRequest
    {
        public Guid? CustomerId { get; init; }
        public OrderStatus? Status { get; init; }
        public decimal? MinTotal { get; init; }
        public DateTime? CreatedAfterUtc { get; init; }
        public string? NotesSearch { get; init; }
        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 25;
    }

    public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

    private sealed record CustomerDetailsDto(Guid CustomerId, string Name, string Email, bool IsActive);

    private sealed record CustomerListItemDto(Guid CustomerId, string Name, string Email, bool IsActive);

    private sealed record OrderListItemDto(int OrderId, Guid CustomerId, decimal Total, OrderStatus Status, DateTime CreatedUtc);
    public sealed record CustomerRevenueDto(Guid CustomerId, string CustomerName, int OrderCount, decimal Revenue, decimal AverageOrderValue);
    public sealed record CustomerHealthDto(Guid CustomerId, string CustomerName, int TotalOrders, decimal AverageOrderValue);
}



