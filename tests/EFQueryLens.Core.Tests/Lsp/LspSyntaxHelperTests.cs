using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public partial class LspSyntaxHelperTests
{
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
