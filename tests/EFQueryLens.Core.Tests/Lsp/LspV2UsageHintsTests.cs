// LspV2UsageHintsTests.cs — unit tests for operator-context hint detection.
// Validates that DetectQueryUsageHints correctly identifies CancellationToken, selector,
// Skip/Take, and string predicate usage patterns in query expressions.
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Parsing;
using Xunit;

namespace EFQueryLens.Core.Tests.Lsp;

public class LspV2UsageHintsTests
{
    [Fact]
    public void DetectQueryUsageHints_ToListAsync_DetectsCancellationToken()
    {
        var expression = "db.Orders.ToListAsync(ct)";
        var hints = LspSyntaxHelper.DetectQueryUsageHints(expression, ["ct"]);

        Assert.True(hints.TryGetValue("ct", out var hint));
        Assert.Equal(QueryUsageHints.CancellationToken, hint);
    }

    [Fact]
    public void DetectQueryUsageHints_ToArrayAsync_DetectsCancellationToken()
    {
        var expression = "db.Users.Where(u => u.IsActive).ToArrayAsync(token)";
        var hints = LspSyntaxHelper.DetectQueryUsageHints(expression, ["token"]);

        Assert.True(hints.TryGetValue("token", out var hint));
        Assert.Equal(QueryUsageHints.CancellationToken, hint);
    }

    [Fact]
    public void DetectQueryUsageHints_SelectExpression_DetectsSelectorExpression()
    {
        var expression = "db.Orders.Select(expression).ToListAsync(ct)";
        var hints = LspSyntaxHelper.DetectQueryUsageHints(expression, ["expression", "ct"]);

        Assert.True(hints.TryGetValue("expression", out var exprHint));
        Assert.Equal(QueryUsageHints.SelectorExpression, exprHint);

        Assert.True(hints.TryGetValue("ct", out var ctHint));
        Assert.Equal(QueryUsageHints.CancellationToken, ctHint);
    }

    [Fact]
    public void DetectQueryUsageHints_SkipAndTake_DetectsSkipTake()
    {
        var expression = "db.Orders.Skip((page - 1) * pageSize).Take(pageSize)";
        var hints = LspSyntaxHelper.DetectQueryUsageHints(expression, ["page", "pageSize"]);

        Assert.True(hints.TryGetValue("page", out var pageHint));
        Assert.Equal(QueryUsageHints.SkipTake, pageHint);

        Assert.True(hints.TryGetValue("pageSize", out var sizeHint));
        Assert.Equal(QueryUsageHints.SkipTake, sizeHint);
    }

    [Fact]
    public void DetectQueryUsageHints_StringStartsWith_DetectsStringPrefix()
    {
        var expression = "db.Customers.Where(c => c.Name.StartsWith(prefix))";
        var hints = LspSyntaxHelper.DetectQueryUsageHints(expression, ["prefix"]);

        Assert.True(hints.TryGetValue("prefix", out var hint));
        Assert.Equal(QueryUsageHints.StringPrefix, hint);
    }

    [Fact]
    public void DetectQueryUsageHints_StringEndsWith_DetectsStringSuffix()
    {
        var expression = "db.Customers.Where(c => c.Email.EndsWith(domain))";
        var hints = LspSyntaxHelper.DetectQueryUsageHints(expression, ["domain"]);

        Assert.True(hints.TryGetValue("domain", out var hint));
        Assert.Equal(QueryUsageHints.StringSuffix, hint);
    }

    [Fact]
    public void DetectQueryUsageHints_StringContains_DetectsStringContains()
    {
        var expression = "db.Posts.Where(p => p.Title.Contains(term))";
        var hints = LspSyntaxHelper.DetectQueryUsageHints(expression, ["term"]);

        Assert.True(hints.TryGetValue("term", out var hint));
        Assert.Equal(QueryUsageHints.StringContains, hint);
    }

    [Fact]
    public void DetectQueryUsageHints_ComplexLiveQuery_DetectsMultipleHints()
    {
        // Matches the live demo query from CustomerReadService
        var expression = """
            _dbContext.Orders
                .OrderByDescending(o => o.CreatedUtc)
                .ThenByDescending(o => o.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(expression)
                .ToListAsync(ct)
            """;

        var hints = LspSyntaxHelper.DetectQueryUsageHints(expression, ["page", "pageSize", "expression", "ct"]);

        Assert.True(hints.TryGetValue("expression", out var exprHint), "expression should have selector hint");
        Assert.Equal(QueryUsageHints.SelectorExpression, exprHint);

        Assert.True(hints.TryGetValue("ct", out var ctHint), "ct should have cancellation hint");
        Assert.Equal(QueryUsageHints.CancellationToken, ctHint);

        Assert.True(hints.TryGetValue("page", out var pageHint), "page should have skip-take hint");
        Assert.Equal(QueryUsageHints.SkipTake, pageHint);

        Assert.True(hints.TryGetValue("pageSize", out var sizeHint), "pageSize should have skip-take hint");
        Assert.Equal(QueryUsageHints.SkipTake, sizeHint);
    }

    [Fact]
    public void DetectQueryUsageHints_EmptyExpression_ReturnsEmptyHints()
    {
        var hints = LspSyntaxHelper.DetectQueryUsageHints("", ["ct"]);

        Assert.Empty(hints);
    }

    [Fact]
    public void DetectQueryUsageHints_NoCapturedNames_ReturnsEmptyHints()
    {
        var hints = LspSyntaxHelper.DetectQueryUsageHints("db.Orders.ToListAsync(ct)", []);

        Assert.Empty(hints);
    }

    [Fact]
    public void DetectQueryUsageHints_UnknownVariable_NotIncluded()
    {
        var expression = "db.Orders.ToListAsync(ct)";
        var hints = LspSyntaxHelper.DetectQueryUsageHints(expression, ["someOtherVar"]);

        Assert.False(hints.ContainsKey("someOtherVar"));
    }
}
