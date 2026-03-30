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
    public void FindAllLinqChains_IfConditionChainDoesNotBleedIntoIfBody()
    {
        // AnyAsync is the condition of the if-statement; ToDictionaryAsync is inside the body.
        // The hover-binding range for AnyAsync must not extend into the if-body.
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

        // AnyAsync chain's hover range must not bleed into the if-body where ToDictionaryAsync lives.
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
    public void TryExtractLinqExpression_CountAsyncInlinePredicate_PassesThroughToEngine()
    {
        // The LSP no longer rewrites terminals — the engine receives the exact expression
        // the app runs and generates accurate SQL from the real CountAsync call.
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
        // The predicate variable is no longer inlined into a Where() — the raw CountAsync
        // expression is sent to the engine, which synthesizes missing identifiers as stubs.
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
        // Local IQueryable variable is still inlined (query root substitution),
        // and the terminal CountAsync is now kept — engine sees the exact expression.
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
        // Cast is stripped and local variable is inlined; the terminal LongCountAsync is kept.
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
        // Hovering inside "x => new Dto { ... }" that is an argument to a wrapper method
        // (not a LINQ chain) previously caused CS0149 by extracting the entire call site
        // as a "LINQ expression" with the method name as the DbContext variable name.
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
        // Where(filter) / Select(selector) with a pre-built Expression<Func<...>> variable
        // is a common EF Core pattern. The extractor must pass the variable name through
        // so the engine can synthesize a proper lambda stub for it.
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

    [Fact]
    public void FindAllLinqChains_AwaitedQueryWithInMemoryToList_StripsOuterInMemoryChain()
    {
        // Regression: (await efQuery.ToListAsync(ct)).ToList() caused CS4032 because
        // the outer chain (larger span) was selected, placing `await` inside the
        // synchronous Run scaffold.  After the fix, only the EF part is kept.
        var source = """
            var queryA = dbContext.Items.Where(x => x.Code.StartsWith("A"));
            var queryB = dbContext.Items.Where(x => x.Code.StartsWith("B"));
            return (await queryA.Concat(queryB).ToListAsync(ct)).ToList();
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);

        // We should find chains for queryA, queryB, and the concat query.
        // None of them should contain a bare `await` in the expression text.
        Assert.All(chains, c => Assert.DoesNotContain("await", c.Expression, StringComparison.Ordinal));

        // The concat chain should have been stripped of the outer in-memory .ToList()
        // and inlined back to the dbContext root.
        var concatChain = chains.FirstOrDefault(c =>
            c.Expression.Contains("ToListAsync", StringComparison.Ordinal)
            && c.Expression.Contains("Concat", StringComparison.Ordinal));
        Assert.NotNull(concatChain);
        Assert.Equal("dbContext", concatChain.ContextVariableName);
        Assert.Contains("dbContext.Items", concatChain.Expression, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", concatChain.Expression, StringComparison.Ordinal);
        // The outer in-memory .ToList() must have been stripped
        Assert.DoesNotContain(".ToList()", concatChain.Expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HoverOnAwaitKeyword_StripsOuterToListReturnsEfChain()
    {
        // Regression: hovering the `await` keyword on "(await query.ToListAsync()).ToList()"
        // extracted the entire "(await ...).ToList()" as the expression, causing CS4032.
        var source = """
            var queryA = dbContext.Items.Where(x => x.Code.StartsWith("A"));
            var queryB = dbContext.Items.Where(x => x.Code.StartsWith("B"));
            return (await queryA.Concat(queryB).ToListAsync(ct)).ToList();
            """;

        var (line, character) = FindPosition(source, "await");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("dbContext.Items", expression, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", expression, StringComparison.Ordinal);
        // The outer in-memory .ToList() must have been stripped
        Assert.DoesNotContain("await", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HoverOnToListAsync_WhenWrappedInAwaitToList_ReturnsEfChain()
    {
        // Hovering on .ToListAsync or .Concat when the query is wrapped in
        // (await ...).ToList() must also return the EF chain, not the outer wrapper.
        var source = """
            var queryA = dbContext.Items.Where(x => x.Code.StartsWith("A"));
            var queryB = dbContext.Items.Where(x => x.Code.StartsWith("B"));
            return (await queryA.Concat(queryB).ToListAsync(ct)).ToList();
            """;

        var (line, character) = FindPosition(source, "ToListAsync");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("ToListAsync", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("await", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_SimpleAwaitedMaterialisation_UnchangedByStripping()
    {
        // Sanity-check: a plain `await db.Items.ToListAsync(ct)` (no outer chain)
        // must be unaffected — StripOuterAwaitChain must not alter it.
        var source = """
            var items = await dbContext.Items
                .Where(x => x.IsActive)
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
        Assert.Contains("dbContext.Items", expression, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("await", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void FindAllLinqChains_StatementStartCoversDeclarationLine()
    {
        // "var x = await" lives before the expression start character on the same line.
        // StatementStartCharacter must be at the "var" column (0), not at "dbContext".
        var source = """
            var itemsWithDistance = await dbContext.CatalogItems
                .Where(c => c.Id > 0)
                .ToListAsync();
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);
        var chain = Assert.Single(chains);

        // Start should be at column 0 (the "var" keyword), not mid-line at "dbContext"
        Assert.Equal(0, chain.StatementStartLine);
        Assert.Equal(0, chain.StatementStartCharacter);
    }

    [Fact]
    public void TryExtractLinqExpression_QueryExpression_FromWhereSelect_ExtractsExpressionAndContext()
    {
        var source = """
            var result = await (from u in dbContext.Users
                                where u.IsActive
                                select u)
                .ToListAsync(ct);
            """;

        var (line, character) = FindPosition(source, "where u.IsActive");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("from u in dbContext.Users", expression, StringComparison.Ordinal);
        Assert.Contains("where u.IsActive", expression, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_QueryExpression_WithJoin_ExtractsContext()
    {
        var source = """
            var result = await (from u in dbContext.Users
                                join r in dbContext.Roles on u.RoleId equals r.Id
                                select new { u.Id, r.Name })
                .ToListAsync(ct);
            """;

        var (line, character) = FindPosition(source, "join r in dbContext.Roles");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("join r in dbContext.Roles", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void FindAllLinqChains_QueryExpressionWithoutTerminal_ReturnsCandidate()
    {
        var source = """
            var query = from u in dbContext.Users
                        where u.IsActive
                        select u;
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);

        var chain = Assert.Single(chains);
        Assert.Equal("dbContext", chain.ContextVariableName);
        Assert.Equal("Users", chain.DbSetMemberName);
        Assert.Contains("from u in dbContext.Users", chain.Expression, StringComparison.Ordinal);
    }

    [Fact]
    public void IsLikelyQueryPreviewCandidate_QueryExpression_ReturnsTrue()
    {
        var candidate = LspSyntaxHelper.IsLikelyQueryPreviewCandidate(
            "from u in dbContext.Users where u.IsActive select u");

        Assert.True(candidate);
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
