using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public class LspSyntaxHelperTests
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
            var query = dbContext.Customers
                .Where(c => c.IsNotDeleted)
                .AsQueryable();

            if (isActive is not null)
            {
                query = query.Where(c => c.IsActive == isActive);
            }

            if (createdAfterUtc is not null)
            {
                var createdAfter = createdAfterUtc.Value;
                query = query.Where(c => c.CreatedUtc >= createdAfter);
            }

            var items = query
                .OrderByDescending(c => c.CreatedUtc)
                .Select(c => new { c.CustomerId, c.Name });
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);

        Assert.NotEmpty(chains);
        Assert.All(chains, c => Assert.Equal("Customers", c.DbSetMemberName));
        Assert.Contains(chains, c => c.Expression.Contains("CreatedUtc >= createdAfter", StringComparison.Ordinal));

        var (isActiveLine, _) = FindPosition(source, "query = query.Where(c => c.IsActive == isActive);");
        var (createdAfterLine, _) = FindPosition(source, "query = query.Where(c => c.CreatedUtc >= createdAfter);");
        // Line is the hover anchor (start of query); this chain should anchor on "var items = query".
        var (returnQueryStartLine, _) = FindPosition(source, "var items = query");

        Assert.Contains(chains, c => c.Line == isActiveLine);
        Assert.Contains(chains, c => c.Line == createdAfterLine);
        Assert.Contains(chains, c => c.Line == returnQueryStartLine);
    }

    [Fact]
    public void TryExtractLinqExpression_UsesRootContextVariable_ForComplexChain()
    {
        var source = """
            var query = context .Orders.AsNoTracking()
                .Where(s => s.IsNotDeleted && s.AccountId == accountId)
                .Select(s => new { s.OrderLine.Status, s.OrderLine.Priority })
                .Distinct();
            """;

        var (line, character) = FindPosition(source, "Distinct");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("context", contextVariableName);
        Assert.StartsWith("context", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HoverInsideLambda_StillUsesRootContextVariable()
    {
        var source = """
            var query = context.Orders
                .Where(s => s.IsNotDeleted && s.AccountId == accountId)
                .Select(s => s.OrderLine.Status)
                .Distinct();
            """;

        var (line, character) = FindPosition(source, "AccountId");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("context", contextVariableName);
        Assert.StartsWith("context", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_ReassignedWhere_ResolvesToDbContextRoot()
    {
        var source = """
            var query = dbContext.Customers
                .Where(c => c.IsNotDeleted)
                .AsQueryable();

            if (isActive is not null)
            {
                query = query.Where(c => c.IsActive == isActive);
            }

            if (createdAfterUtc is not null)
            {
                var createdAfter = createdAfterUtc.Value;
                query = query.Where(c => c.CreatedUtc >= createdAfter);
            }
            """;

        var (line, character) = FindPosition(source, "CreatedUtc >= createdAfter");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Customers", expression, StringComparison.Ordinal);
        Assert.Contains("CreatedUtc >= createdAfter", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_CountAsyncInlinePredicate_PreservesPredicateAsWhere()
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
        Assert.Contains(".Where(", expression, StringComparison.Ordinal);
        Assert.Contains("ApplicationId != applicationId", expression, StringComparison.Ordinal);
        Assert.Contains(".GroupBy(_ => 1).Select(g => g.Count())", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_CountAsyncPredicateVariable_PreservesPredicateAsWhere()
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
        Assert.Contains(".Where(countPredicate)", expression, StringComparison.Ordinal);
        Assert.Contains(".GroupBy(_ => 1).Select(g => g.Count())", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_CountAsyncLocalPredicateVariable_InlinesLambdaExpression()
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
        Assert.Contains("ApplicationId != applicationId", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("Where(countPredicate)", expression, StringComparison.Ordinal);
        Assert.Contains(".GroupBy(_ => 1).Select(g => g.Count())", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_CountAsyncOnLocalQueryVariable_InlinesQuerySource()
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
        Assert.Contains(".GroupBy(_ => 1).Select(g => g.Count())", expression, StringComparison.Ordinal);
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
    public void TryExtractLinqExpression_LongCountOnCastedQueryableLocal_StripsTransparentTypeCast()
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
        Assert.Contains(".GroupBy(_ => 1).Select(g => g.LongCount())", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_AnyAsync_RewritesToTakeOne()
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
        Assert.Contains(".Take(1)", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_FirstAsyncPredicate_RewritesToWhereTakeOne()
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
        Assert.Contains(".Where(", expression, StringComparison.Ordinal);
        Assert.Contains(".Take(1)", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_SingleAsyncPredicateVariable_RewritesToWhereTakeTwo()
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
        Assert.Contains(".Where(matchExpr)", expression, StringComparison.Ordinal);
        Assert.Contains(".Take(2)", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractUsingContext_CollectsImportsAliasesAndStaticUsings()
    {
        var source = """
            using System.Linq;
            using Enums = My.Application.Core.Entities.Enums;
            using static System.Math;

            namespace Demo;

            internal sealed class C;
            """;

        var context = LspSyntaxHelper.ExtractUsingContext(source);

        Assert.Contains("System.Linq", context.Imports);
        Assert.Contains("System.Math", context.StaticTypes);
        Assert.True(context.Aliases.TryGetValue("Enums", out var aliasTarget));
        Assert.Equal("My.Application.Core.Entities.Enums", aliasTarget);
    }

    [Fact]
    public void ExtractUsingContext_DeduplicatesRepeatedImports()
    {
        var source = """
            using System.Linq;
            using System.Linq;
            using static System.Math;
            using static System.Math;

            namespace Demo;

            internal sealed class C;
            """;

        var context = LspSyntaxHelper.ExtractUsingContext(source);

        Assert.Single(context.Imports, i => i == "System.Linq");
        Assert.Single(context.StaticTypes, s => s == "System.Math");
    }

    [Fact]
    public void IsLikelyQueryPreviewCandidate_QueryChain_ReturnsTrue()
    {
        var candidate = LspSyntaxHelper.IsLikelyQueryPreviewCandidate(
            "dbContext.Customers.Where(c => c.IsActive)");

        Assert.True(candidate);
    }

    [Fact]
    public void IsLikelyQueryPreviewCandidate_NonQueryInvocation_ReturnsFalse()
    {
        var candidate = LspSyntaxHelper.IsLikelyQueryPreviewCandidate(
            "Math.Max(request.Page, 1)");

        Assert.False(candidate);
    }

    [Fact]
    public void IsLikelyDbContextRootIdentifier_UnderscoreDb_ReturnsTrue()
    {
        var candidate = LspSyntaxHelper.IsLikelyDbContextRootIdentifier("_db");

        Assert.True(candidate);
    }

    [Fact]
    public void IsLikelyDbContextRootIdentifier_Service_ReturnsFalse()
    {
        var candidate = LspSyntaxHelper.IsLikelyDbContextRootIdentifier("service");

        Assert.False(candidate);
    }

    private static (int line, int character) FindPosition(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Marker '{marker}' not found in source text.");

        var line = 0;
        var character = 0;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return (line, character);
    }
}
