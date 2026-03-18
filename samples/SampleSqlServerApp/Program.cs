using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SampleSqlServerApp.Application;
using SampleSqlServerApp.Application.Customers;
using SampleSqlServerApp.Application.Orders;
using SampleSqlServerApp.Domain.Entities;
using SampleSqlServerApp.Domain.Enums;
using SampleSqlServerApp.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
	.AddApplication()
	.AddInfrastructure(builder.Configuration);

var app = builder.Build();

var sampleCustomerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

app.MapGet("/", () => Results.Ok(new { Message = "SampleSqlServerApp API is running." }));

app.MapGet("/api/customers/{customerId:guid}",
	async (Guid customerId, CustomerReadService service, CancellationToken ct) =>
	{
		var customer = await service.GetCustomerByIdAsync(
			customerId,
			c => new CustomerDetailsResponse
			{
				CustomerId = c.CustomerId,
				Name = c.Name,
				Email = c.Email,
				IsActive = c.IsActive,
				TotalOrders = c.Orders.Count(o => o.IsNotDeleted)
			},
			ct);

		return customer is null ? Results.NotFound() : Results.Ok(customer);
	});

app.MapGet("/api/customers",
	async (
		string? searchTerm,
		bool? isActive,
		DateTime? createdAfterUtc,
		int? minOrders,
		int page,
		int pageSize,
		CustomerReadService service,
		CancellationToken ct) =>
	{
		var request = new CustomerReadService.CustomerQueryRequest
		{
			SearchTerm = searchTerm,
			IsActive = isActive,
			CreatedAfterUtc = createdAfterUtc,
			MinOrders = minOrders,
			Page = page <= 0 ? 1 : page,
			PageSize = pageSize <= 0 ? 25 : pageSize
		};

		var customers = await service.GetCustomersAsync(
			request,
			c => new CustomerListItemResponse
			{
				CustomerId = c.CustomerId,
				Name = c.Name,
				Email = c.Email,
				IsActive = c.IsActive
			},
			ct);

		return Results.Ok(customers);
	});

app.MapGet("/api/customers/{customerId:guid}/orders",
	async (
		Guid customerId,
		decimal? minTotal,
		OrderStatus? status,
		CustomerReadService service,
		CancellationToken ct) =>
	{
		Expression<Func<Order, bool>> whereExpression = o =>
			(!minTotal.HasValue || o.Total >= minTotal.Value)
			&& (!status.HasValue || o.Status == status.Value);

		var orders = await service.GetCustomerOrdersAsync(
			customerId,
			whereExpression,
			o => new OrderListItemResponse
			{
				OrderId = o.Id,
				CustomerId = o.Customer.CustomerId,
				Total = o.Total,
				Status = o.Status,
				CreatedUtc = o.CreatedUtc
			},
			ct);

		return Results.Ok(orders);
	});

app.MapGet("/api/orders/paged",
	async (
		Guid? customerId,
		OrderStatus? status,
		decimal? minTotal,
		DateTime? createdAfterUtc,
		string? notesSearch,
		int page,
		int pageSize,
		CustomerReadService service,
		CancellationToken ct) =>
	{
		var request = new CustomerReadService.OrderQueryRequest
		{
			CustomerId = customerId,
			Status = status,
			MinTotal = minTotal,
			CreatedAfterUtc = createdAfterUtc,
			NotesSearch = notesSearch,
			Page = page <= 0 ? 1 : page,
			PageSize = pageSize <= 0 ? 25 : pageSize
		};

		var result = await service.GetPagedOrdersAsync(
			request,
			o => new OrderListItemResponse
			{
				OrderId = o.Id,
				CustomerId = o.Customer.CustomerId,
				Total = o.Total,
				Status = o.Status,
				CreatedUtc = o.CreatedUtc
			},
			ct);

		return Results.Ok(result);
	});

app.MapGet("/api/orders/recent",
	async (
		int? days,
		OrderQueries orderQueries,
		CancellationToken ct) =>
	{
		var lookbackDays = days ?? 30;
		var recentOrders = await orderQueries
			.BuildRecentOrdersQuery(DateTime.UtcNow, lookbackDays)
			.Select(o => new RecentOrderResponse
			{
				OrderId = o.OrderId,
				CustomerName = o.CustomerName,
				Total = o.Total,
				CreatedUtc = o.CreatedUtc
			})
			.ToListAsync(ct);

		return Results.Ok(recentOrders);
	});

app.MapGet("/api/orders/recent/sql",
	(int? days, OrderQueries orderQueries) =>
	{
		var lookbackDays = days ?? 30;
		var query = orderQueries
			.BuildRecentOrdersQuery(DateTime.UtcNow, lookbackDays)
			.Select(o => new RecentOrderResponse
			{
				OrderId = o.OrderId,
				CustomerName = o.CustomerName,
				Total = o.Total,
				CreatedUtc = o.CreatedUtc
			});

		return Results.Ok(new SqlPreviewItem("Recent Orders", query.ToQueryString()));
	});

app.MapGet("/api/sql-preview",
	(Guid? customerId, CustomerReadService service) =>
	{
		var targetCustomerId = customerId ?? sampleCustomerId;
		var sqlItems = service
			.BuildSqlPreviewCatalog(targetCustomerId, DateTime.UtcNow)
			.Select(x => new SqlPreviewItem(x.Title, x.Query.ToQueryString()))
			.ToArray();

		return Results.Ok(sqlItems);
	});

app.Run();

public sealed class CustomerDetailsResponse
{
	public Guid CustomerId { get; init; }
	public string Name { get; init; } = string.Empty;
	public string Email { get; init; } = string.Empty;
	public bool IsActive { get; init; }
	public int TotalOrders { get; init; }
}

public sealed class CustomerListItemResponse
{
	public Guid CustomerId { get; init; }
	public string Name { get; init; } = string.Empty;
	public string Email { get; init; } = string.Empty;
	public bool IsActive { get; init; }
}

public sealed class OrderListItemResponse
{
	public int OrderId { get; init; }
	public Guid CustomerId { get; init; }
	public decimal Total { get; init; }
	public OrderStatus Status { get; init; }
	public DateTime CreatedUtc { get; init; }
}

public sealed class RecentOrderResponse
{
	public int OrderId { get; init; }
	public string CustomerName { get; init; } = string.Empty;
	public decimal Total { get; init; }
	public DateTime CreatedUtc { get; init; }
}

public sealed record SqlPreviewItem(string Title, string Sql);
