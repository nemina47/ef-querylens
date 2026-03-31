using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public partial class LspSyntaxHelperTests
{
    [Fact]
    public void FindAllLinqChains_FindsTopLevelTerminalQueries()
    {
        var source = """
            var first = await dbContext.Applications
                .Where(a => a.ApplicationId == appId)
                .ToListAsync(ct);

            var second = await dbContext.AuditTrails
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);

        Assert.Equal(2, chains.Count);
        var dbSets = chains.Select(c => c.DbSetMemberName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Applications", dbSets);
        Assert.Contains("AuditTrails", dbSets);
        Assert.All(chains, c => Assert.Equal("dbContext", c.ContextVariableName));
    }

    [Fact]
    public void FindAllLinqChains_SkipsNestedQueriesInsideLambda()
    {
        var source = """
            var query = await dbContext.Parents
                .Select(p => new
                {
                    p.Id,
                    Child = dbContext.Children.Where(c => c.ParentId == p.Id).FirstOrDefault()
                })
                .ToListAsync(ct);
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);

        Assert.Single(chains);
        Assert.Equal("Parents", chains[0].DbSetMemberName);
        Assert.DoesNotContain(chains, c => c.DbSetMemberName == "Children");
    }

    [Fact]
    public void FindAllLinqChains_FindsTopLevelNonTerminalQueryChains()
    {
        var source = """
            var query = dbContext.CaseCloseHistory
                .Where(c => c.PlusCaseId == caseId)
                .Select(c => new { c.OfficerId, c.CreatedAt });
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);

        Assert.Single(chains);
        Assert.Equal("CaseCloseHistory", chains[0].DbSetMemberName);
        Assert.Equal("dbContext", chains[0].ContextVariableName);
        Assert.Contains("Select", chains[0].Expression, StringComparison.Ordinal);
    }

    [Fact]
    public void FindAllLinqChains_IfConditionChainDoesNotBleedIntoIfBody()
    {
        var source = """
            if (!await context.CatalogItems.AnyAsync())
            {
                var ids = await context.CatalogTypes.ToDictionaryAsync(x => x.Type, x => x.Id);
            }
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);

        Assert.Equal(2, chains.Count);
        var anyChain = chains.Single(c => c.Expression.Contains("AnyAsync"));
        var dictChain = chains.Single(c => c.Expression.Contains("ToDictionaryAsync"));

        Assert.True(anyChain.StatementEndLine < dictChain.StatementStartLine,
            $"AnyAsync StatementEndLine ({anyChain.StatementEndLine}) must be before " +
            $"ToDictionaryAsync StatementStartLine ({dictChain.StatementStartLine})");
    }

    [Fact]
    public void FindAllLinqChains_DoesNotTreatMapGetRegistrationAsQueryChain()
    {
        var source = """
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
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);

        Assert.Empty(chains);
    }

    [Fact]
    public void FindAllLinqChains_ReassignedLocalQuery_ResolvesDbSetMember()
    {
        var source = """
            var baseQuery = db.ApplicationChecklists
                .Where(a => !a.IsDeleted);

            var filtered = baseQuery
                .Where(a => a.ApplicationId == appId)
                .OrderByDescending(a => a.CreatedAt);

            await filtered.ToListAsync(ct);
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);

        Assert.NotEmpty(chains);

        var terminal = chains.SingleOrDefault(c =>
            c.Expression.Contains("ToListAsync", StringComparison.Ordinal));

        Assert.NotNull(terminal);
        Assert.Equal("ApplicationChecklists", terminal.DbSetMemberName);
        Assert.Equal("db", terminal.ContextVariableName);
    }
}
