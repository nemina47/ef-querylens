namespace SampleMySqlApp.Application.Orders;

public sealed record OrderSummaryDto(
    int OrderId,
    string CustomerName,
    decimal Total,
    DateTime CreatedUtc);
