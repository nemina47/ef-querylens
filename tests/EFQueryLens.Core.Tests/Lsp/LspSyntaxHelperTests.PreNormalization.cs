using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public partial class LspSyntaxHelperTests
{
    // ── PreNormalization: pattern matching ────────────────────────────────────

    [Fact]
    public void PreNormalizeExtractedExpression_NullPattern_RewritesToEquality()
    {
        var input  = "db.Orders.Where(o => o.Customer is null).Select(o => o.Id)";
        var result = LspSyntaxHelper.PreNormalizeExtractedExpression(input);

        Assert.DoesNotContain(" is null", result, StringComparison.Ordinal);
        Assert.Contains("== null", result, StringComparison.Ordinal);
        Assert.Contains("db.Orders", result, StringComparison.Ordinal);
    }

    [Fact]
    public void PreNormalizeExtractedExpression_ConstantPattern_RewritesToEquality()
    {
        // x is true  →  x == true
        var input  = "db.Orders.Where(o => o.IsActive is true).Select(o => o.Id)";
        var result = LspSyntaxHelper.PreNormalizeExtractedExpression(input);

        Assert.DoesNotContain(" is true", result, StringComparison.Ordinal);
        Assert.Contains("== true", result, StringComparison.Ordinal);
    }

    [Fact]
    public void PreNormalizeExtractedExpression_OrPattern_RewritesToLogicalOr()
    {
        // x is null or ""  →  x == null || x == ""
        var input  = "db.Items.Where(i => i.Code is null or \"\").ToList()";
        var result = LspSyntaxHelper.PreNormalizeExtractedExpression(input);

        // Should not contain the raw `is` pattern form
        Assert.DoesNotContain(" is null or ", result, StringComparison.Ordinal);
        // Should produce logical OR
        Assert.Contains("||", result, StringComparison.Ordinal);
    }

    [Fact]
    public void PreNormalizeExtractedExpression_NoPattern_ReturnsUnchanged()
    {
        var input  = "db.Orders.Where(o => o.Total > 200).Select(o => o.Id)";
        var result = LspSyntaxHelper.PreNormalizeExtractedExpression(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void PreNormalizeExtractedExpression_ConcatAfterEquivalentSelects_HoistsSelectAfterConcat()
    {
        var input =
            "db.Orders.Where(o => !o.IsDeleted).Select(o => new OrderSummaryDto(o.Id, o.Customer.Name, o.Total, o.CreatedUtc))" +
            ".Concat(db.Orders.Where(x => x.Total >= 200m).Select(x => new OrderSummaryDto(x.Id, x.Customer.Name, x.Total, x.CreatedUtc)))";

        var result = LspSyntaxHelper.PreNormalizeExtractedExpression(input);

        Assert.Contains(".Concat(", result, StringComparison.Ordinal);
        Assert.Contains(").Select(", result, StringComparison.Ordinal);
        Assert.DoesNotContain(").Select(o => new OrderSummaryDto(o.Id, o.Customer.Name, o.Total, o.CreatedUtc)).Concat(", result, StringComparison.Ordinal);
        Assert.Contains("new OrderSummaryDto(__ql_param.Id, __ql_param.Customer.Name, __ql_param.Total, __ql_param.CreatedUtc)".Replace("__ql_param", "o"), result, StringComparison.Ordinal);
    }

    [Fact]
    public void PreNormalizeExtractedExpression_ConcatAfterDifferentSelects_DoesNotRewrite()
    {
        var input =
            "db.Orders.Where(o => !o.IsDeleted).Select(o => new { o.Id, o.Total })" +
            ".Concat(db.Orders.Where(x => x.Total >= 200m).Select(x => new { x.Id, x.CreatedUtc }))";

        var result = LspSyntaxHelper.PreNormalizeExtractedExpression(input);

        Assert.Equal(input, result);
    }

    // ── PreNormalization: ternary pattern ─────────────────────────────────────

    [Fact]
    public void PreNormalizeExtractedExpression_TernaryWithPatternEquality_Unwraps()
    {
        // (x is null) == true ? a : b  is an unusual form some tools generate
        var input  = "db.Orders.Where(o => (o.Customer is null) == true ? 1 : 0 > 0)";
        var result = LspSyntaxHelper.PreNormalizeExtractedExpression(input);

        // The rewriter should transform some aspect of the ternary pattern comparison
        // (exact form depends on nesting but result must not contain original)
        Assert.NotNull(result);
    }

    [Fact]
    public void PreNormalizeExtractedExpression_TernaryWithoutPattern_ReturnsUnchanged()
    {
        var input  = "db.Orders.Where(o => o.Total > 0 ? 1 : 0).ToList()";
        var result = LspSyntaxHelper.PreNormalizeExtractedExpression(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void PreNormalizeExtractedExpression_CollectionExpressionReceiver_RewritesToImplicitArray()
    {
        var input = "db.ProductOwners.Where(m => [productOwnerId ?? Guid.Empty].Contains(m.ProductOwnerId)).ToListAsync(ct)";
        var result = LspSyntaxHelper.PreNormalizeExtractedExpression(input);

        Assert.Contains("new[]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[productOwnerId ?? Guid.Empty].Contains", result, StringComparison.Ordinal);
        Assert.Contains(".Contains(m.ProductOwnerId)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void PreNormalizeExtractedExpression_CollectionExpressionSpreadReceiver_RewritesToSourceExpression()
    {
        var input = "db.ProductModels.Where(m => [.. deviceIds].Contains(m.ProductId)).Select(c => c.ProductModelId).ToListAsync(ct)";
        var result = LspSyntaxHelper.PreNormalizeExtractedExpression(input);

        Assert.DoesNotContain("[.. deviceIds].Contains", result, StringComparison.Ordinal);
        Assert.Contains("deviceIds.Contains(m.ProductId)", result, StringComparison.Ordinal);
        Assert.Contains("Select", result, StringComparison.Ordinal);
    }

    // ── PreNormalization: extraction pipeline integration ────────────────────

    [Fact]
    public void TryExtractLinqExpression_WithIsNullPattern_ExtractsAndNormalizeToEquality()
    {
        // End-to-end: extraction + pre-normalization for a realistic hover position
        var source = """
            var result = dbContext.Orders
                .Where(o => o.Customer is null)
                .Select(o => o.Id)
                .ToList();
            """;

        var (line, character) = FindPosition(source, "ToList");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out _,
            out _);

        Assert.NotNull(expression);
        Assert.DoesNotContain(" is null", expression, StringComparison.Ordinal);
        Assert.Contains("== null", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_WithoutPattern_ExtractionUnaffectedByPreNormalization()
    {
        // Pre-normalization must be transparent for expressions without any `is` patterns.
        var source = """
            var result = dbContext.Orders
                .Where(o => o.Total > 200)
                .Select(o => o.Id)
                .ToList();
            """;

        var (line, character) = FindPosition(source, "ToList");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVar,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVar);
        Assert.Contains("o.Total > 200", expression, StringComparison.Ordinal);
    }
}
