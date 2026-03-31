using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public partial class LspSyntaxHelperTests
{
    [Fact]
    public void FindAllLinqChains_AwaitedQueryWithInMemoryToList_StripsOuterInMemoryChain()
    {
        var source = """
            var queryA = dbContext.Items.Where(x => x.Code.StartsWith("A"));
            var queryB = dbContext.Items.Where(x => x.Code.StartsWith("B"));
            return (await queryA.Concat(queryB).ToListAsync(ct)).ToList();
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);

        Assert.All(chains, c => Assert.DoesNotContain("await", c.Expression, StringComparison.Ordinal));

        var concatChain = chains.FirstOrDefault(c =>
            c.Expression.Contains("ToListAsync", StringComparison.Ordinal)
            && c.Expression.Contains("Concat", StringComparison.Ordinal));
        Assert.NotNull(concatChain);
        Assert.Equal("dbContext", concatChain.ContextVariableName);
        Assert.Contains("dbContext.Items", concatChain.Expression, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", concatChain.Expression, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToList()", concatChain.Expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HoverOnAwaitKeyword_StripsOuterToListReturnsEfChain()
    {
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
        Assert.DoesNotContain("await", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_HoverOnToListAsync_WhenWrappedInAwaitToList_ReturnsEfChain()
    {
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
        var source = """
            var itemsWithDistance = await dbContext.CatalogItems
                .Where(c => c.Id > 0)
                .ToListAsync();
            """;

        var chains = LspSyntaxHelper.FindAllLinqChains(source);
        var chain = Assert.Single(chains);

        Assert.Equal(0, chain.StatementStartLine);
        Assert.Equal(0, chain.StatementStartCharacter);
    }
}
