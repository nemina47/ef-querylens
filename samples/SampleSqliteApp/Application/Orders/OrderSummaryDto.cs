using SampleSqliteApp.Domain.Enums;

namespace SampleSqliteApp.Application.Orders;

public sealed record OrderSummaryDto(
    int OrderId,
    string CustomerName,
    decimal Total,
    OrderStatus Status,
    DateTime CreatedUtc);
