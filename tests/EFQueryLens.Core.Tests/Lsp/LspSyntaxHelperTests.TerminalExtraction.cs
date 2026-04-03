using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public partial class LspSyntaxHelperTests
{
    [Fact]
    public void TryExtractLinqExpression_CountAsyncInlinePredicate_PassesThroughToEngine()
    {
        var source = """
            var count = await dbContext.Applications
                .CountAsync(w => w.ApplicationId != applicationId, ct);
            """;

        var (line, character) = FindPosition(source, "CountAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Applications", expression, StringComparison.Ordinal);
        Assert.Contains("CountAsync", expression, StringComparison.Ordinal);
        Assert.Contains("ApplicationId != applicationId", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_CountAsyncPredicateVariable_PassesThroughToEngine()
    {
        var source = """
            var count = await dbContext.Applications
                .CountAsync(countPredicate, ct);
            """;

        var (line, character) = FindPosition(source, "CountAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Applications", expression, StringComparison.Ordinal);
        Assert.Contains("CountAsync", expression, StringComparison.Ordinal);
        Assert.Contains("countPredicate", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_CountAsyncLocalPredicateVariable_PassesThroughToEngine()
    {
        var source = """
            Expression<Func<Entities.Application, bool>> countPredicate = w =>
                w.SubmittedAt != null
                && w.SubmittedAt!.Value.Date == dateTime.Now.Date
                && w.ApplicationId != applicationId;

            var count = await dbContext.Applications.CountAsync(countPredicate, ct);
            """;

        var (line, character) = FindPosition(source, "CountAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Applications", expression, StringComparison.Ordinal);
        Assert.Contains("CountAsync", expression, StringComparison.Ordinal);
        Assert.Contains("countPredicate", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_CountAsyncOnLocalQueryVariable_InlinesQuerySourceAndKeepsTerminal()
    {
        var source = """
            var auditTrailQuery = dbContext.Applications
                .AsNoTracking()
                .Where(s => s.ApplicationId == applicationId)
                .Select(s => new { s.ApplicationId, s.SubmittedAt });

            var count = await auditTrailQuery.CountAsync(ct);
            """;

        var (line, character) = FindPosition(source, "CountAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.DoesNotContain("auditTrailQuery", expression, StringComparison.Ordinal);
        Assert.Contains("Applications", expression, StringComparison.Ordinal);
        Assert.Contains("CountAsync", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_ToListAsyncAfterApplyPagingOnLocalQuery_InlinesQuerySource()
    {
        var source = """
            var auditTrailQuery = dbContext.Applications
                .AsNoTracking()
                .Where(s => s.ApplicationId == applicationId)
                .Select(s => new { s.ApplicationId, s.SubmittedAt });

            var items = await auditTrailQuery.ApplyPaging(query.Page, query.PageSize).ToListAsync(ct);
            """;

        var (line, character) = FindPosition(source, "ToListAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.DoesNotContain("auditTrailQuery", expression, StringComparison.Ordinal);
        Assert.Contains("Applications", expression, StringComparison.Ordinal);
        Assert.Contains("ApplyPaging(query.Page, query.PageSize)", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_LongCountOnCastedQueryableLocal_StripsTransparentTypeCastAndKeepsTerminal()
    {
        var source = """
            var root = (IQueryable<CatalogItem>)services.Context.CatalogItems;
            var totalItems = await root.LongCountAsync();
            """;

        var (line, character) = FindPosition(source, "LongCountAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("services", contextVariableName);
        Assert.Contains("services.Context.CatalogItems", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("IQueryable<CatalogItem>", expression, StringComparison.Ordinal);
        Assert.Contains("LongCountAsync", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_AnyAsync_PassesThroughToEngine()
    {
        var source = """
            var exists = await dbContext.Applications.AnyAsync(ct);
            """;

        var (line, character) = FindPosition(source, "AnyAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Applications", expression, StringComparison.Ordinal);
        Assert.Contains("AnyAsync", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_FirstAsyncPredicate_PassesThroughToEngine()
    {
        var source = """
            var entity = await dbContext.Applications.FirstAsync(a => a.ApplicationId == applicationId, ct);
            """;

        var (line, character) = FindPosition(source, "FirstAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Applications", expression, StringComparison.Ordinal);
        Assert.Contains("FirstAsync", expression, StringComparison.Ordinal);
        Assert.Contains("applicationId", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_SingleAsyncPredicateVariable_PassesThroughToEngine()
    {
        var source = """
            var entity = await dbContext.Applications.SingleAsync(matchExpr, ct);
            """;

        var (line, character) = FindPosition(source, "SingleAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Applications", expression, StringComparison.Ordinal);
        Assert.Contains("SingleAsync", expression, StringComparison.Ordinal);
        Assert.Contains("matchExpr", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HoverInsideLambdaArgOfNonLinqMethod_ReturnsNull()
    {
        var source = """
            var result = await service.GetOrdersAsync(
                customerId,
                x => new OrderSummaryDto
                {
                    Id = x.Id,
                    Total = x.Total
                },
                ct);
            """;

        var (line, character) = FindPosition(source, "Total = x.Total");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.Null(expression);
        Assert.Null(contextVariableName);
    }

    [Fact]
    public void TryExtractLinqExpression_HelperMethodWithSelectorExpression_SynthesizesQueryableChain()
    {
        var source = """
            public class DemoService
            {
                private async Task<List<TResult>> GetOrdersAsync<TResult>(
                    Guid customerId,
                    Expression<Func<Order, TResult>> selector,
                    CancellationToken ct)
                {
                    return await dbContext.Orders
                        .Where(o => o.CustomerId == customerId)
                        .Select(selector)
                        .ToListAsync(ct);
                }

                private async Task Run(Guid customerId, CancellationToken ct)
                {
                    var result = await GetOrdersAsync(
                        customerId,
                        o => new { o.Id, o.Total },
                        ct);
                }
            }
            """;

        var (line, character) = FindPosition(source, "o.Total");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Orders", expression, StringComparison.Ordinal);
        Assert.Contains("o => new { o.Id, o.Total }", expression, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HelperMethodWithWhereAndSelectorExpressions_SynthesizesQueryableChain()
    {
        var source = """
            public class DemoService
            {
                private async Task<List<TResult>> GetOrdersAsync<TResult>(
                    Guid customerId,
                    Expression<Func<Order, bool>> whereExpression,
                    Expression<Func<Order, TResult>> selector,
                    CancellationToken ct)
                {
                    return await dbContext.Orders
                        .Where(o => o.CustomerId == customerId)
                        .Where(whereExpression)
                        .Select(selector)
                        .ToListAsync(ct);
                }

                private async Task Run(Guid customerId, CancellationToken ct)
                {
                    var result = await GetOrdersAsync(
                        customerId,
                        o => o.IsNotDeleted,
                        o => new { o.Id, o.Total },
                        ct);
                }
            }
            """;

        var (line, character) = FindPosition(source, "o.IsNotDeleted");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Orders", expression, StringComparison.Ordinal);
        Assert.Contains("o => o.IsNotDeleted", expression, StringComparison.Ordinal);
        Assert.Contains("o => new { o.Id, o.Total }", expression, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HelperMethodReturningQueryable_InlinesUnderlyingDbQuery()
    {
        var source = """
            public sealed class ProgramLike
            {
                public async Task Run(OrderQueries orderQueries, CancellationToken ct)
                {
                    var result = await orderQueries
                        .BuildRecentOrdersQuery(DateTime.UtcNow, 30)
                        .Select(o => new { o.OrderId, o.Total })
                        .ToListAsync(ct);
                }
            }
            
            public sealed class OrderQueries
            {
                private readonly IMySqlAppDbContext _db;

                public OrderQueries(IMySqlAppDbContext db)
                {
                    _db = db;
                }

                public IQueryable<OrderSummaryDto> BuildRecentOrdersQuery(DateTime utcNow, int lookbackDays = 30)
                {
                    var fromUtc = utcNow.Date.AddDays(-lookbackDays);
                    return _db.Orders
                        .Where(o => o.IsNotDeleted && o.CreatedUtc >= fromUtc)
                        .Select(o => new OrderSummaryDto(o.Id, o.Customer.Name, o.Total, o.CreatedUtc));
                }
            }
            """;

        var (line, character) = FindPosition(source, "BuildRecentOrdersQuery");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("_db", contextVariableName);
        Assert.Contains("_db.Orders", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("orderQueries.BuildRecentOrdersQuery", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HelperMethodReturningQueryable_PreservesHelperFreeVariablesUsedInSkipTake()
    {
        var source = """
            public sealed class DemoService
            {
                public IQueryable<Customer> GetCustomersQuery(CustomerQueryRequest request)
                {
                    var page = Math.Max(request.Page, 1);
                    var pageSize = Math.Clamp(request.PageSize, 1, 200);

                    return _dbContext.Customers
                        .Where(c => c.IsNotDeleted)
                        .OrderByDescending(c => c.CreatedUtc)
                        .Skip((page - 1) * pageSize)
                        .Take(pageSize);
                }

                public IQueryable<Customer> Run(CustomerQueryRequest customerQuery)
                {
                    return GetCustomersQuery(customerQuery);
                }
            }
            """;

        var (line, character) = FindPosition(source, "GetCustomersQuery(customerQuery)");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("_dbContext", contextVariableName);
        Assert.DoesNotContain("GetCustomersQuery", expression, StringComparison.Ordinal);
        Assert.Contains("Skip((page - 1) * pageSize)", expression, StringComparison.Ordinal);
        Assert.Contains(".Take(pageSize)", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HelperMethodReturningQueryable_HoverOnAssignedVariable_StillInlinesUnderlyingDbQuery()
    {
        var source = """
            public sealed class ProgramLike
            {
                public void Run(OrderQueries orderQueries, Guid customerId)
                {
                    var customerOrders = GetCustomerOrdersQuery(
                        customerId,
                        o => o.Total >= 100 && o.Status != OrderStatus.Cancelled,
                        o => new OrderListItemDto(o.Id, o.Customer.CustomerId, o.Total, o.Status, o.CreatedUtc));
                }

                private static IQueryable<OrderListItemDto> GetCustomerOrdersQuery(
                    Guid customerId,
                    Expression<Func<Order, bool>> whereExpression,
                    Expression<Func<Order, OrderListItemDto>> selector)
                {
                    return _dbContext
                        .Orders
                        .Where(o => o.Customer.CustomerId == customerId)
                        .Where(o => o.IsNotDeleted)
                        .Where(whereExpression)
                        .Select(selector);
                }
            }
            """;

        var (line, character) = FindPosition(source, "customerOrders");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("_dbContext", contextVariableName);
        Assert.Contains("_dbContext", expression, StringComparison.Ordinal);
        Assert.Contains(".Orders", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCustomerOrdersQuery", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HelperMethodWithLocalExpressionVariable_InlinesVariableExpression()
    {
        var source = """
            public class DemoService
            {
                private async Task<List<TResult>> GetOrdersAsync<TResult>(
                    Guid customerId,
                    Expression<Func<Order, bool>> whereExpression,
                    Expression<Func<Order, TResult>> selector,
                    CancellationToken ct)
                {
                    return await dbContext.Orders
                        .Where(o => o.CustomerId == customerId)
                        .Where(whereExpression)
                        .Select(selector)
                        .ToListAsync(ct);
                }

                private async Task Run(Guid customerId, CancellationToken ct)
                {
                    Expression<Func<Order, bool>> whereExpression = o => !o.IsDeleted;
                    var result = await GetOrdersAsync(
                        customerId,
                        whereExpression,
                        o => new { o.Id, o.Total },
                        ct);
                }
            }
            """;

        var (line, character) = FindPosition(source, """
            whereExpression,
                        o => new { o.Id, o.Total }
            """);

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Orders", expression, StringComparison.Ordinal);
        Assert.Contains("o => !o.IsDeleted", expression, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(whereExpression)", expression, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_WhereClauseReceivesExpressionVariable_ExtractsChainAndIncludesVariable()
    {
        var source = """
            Expression<Func<Order, bool>> filter = o => o.IsNotDeleted;

            var result = await dbContext.Orders
                .Where(filter)
                .ToListAsync(ct);
            """;

        var (line, character) = FindPosition(source, "ToListAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Orders", expression, StringComparison.Ordinal);
        Assert.Contains("filter", expression, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", expression, StringComparison.Ordinal);
    }
}
