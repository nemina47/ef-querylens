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
            out var contextVariableName);

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
            out var contextVariableName);

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
            out var contextVariableName);

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
            out var contextVariableName);

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
            out var contextVariableName);

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
            out var contextVariableName);

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
            out var contextVariableName);

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
            out var contextVariableName);

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
            out var contextVariableName);

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
            out var contextVariableName);

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
            out var contextVariableName);

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
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Orders", expression, StringComparison.Ordinal);
        Assert.Contains("o => o.IsNotDeleted", expression, StringComparison.Ordinal);
        Assert.Contains("o => new { o.Id, o.Total }", expression, StringComparison.Ordinal);
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
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Orders", expression, StringComparison.Ordinal);
        Assert.Contains("filter", expression, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", expression, StringComparison.Ordinal);
    }
}
