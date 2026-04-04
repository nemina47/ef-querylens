using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public partial class LspSyntaxHelperTests
{
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
            out var contextVariableName,
            out _);

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
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("dbContext", contextVariableName);
        Assert.Contains("join r in dbContext.Roles", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpression_QueryExpression_FromLocalQueryableRoot_InlinesToDbContextRoot()
    {
        var source = """
            var query = _context.PlusCases
                .Where(w => w.IsNotDeleted());

            var dashboard = await (
                from @case in query
                select new { @case.CaseStatus, @case.CaseType }
            ).SingleOrDefaultAsync(cancellationToken);
            """;

        var (line, character) = FindPosition(source, "from @case in query");

        var expression = LspSyntaxHelper.TryExtractLinqExpression(
            source,
            line,
            character,
            out var contextVariableName,
            out _);

        Assert.NotNull(expression);
        Assert.Equal("_context", contextVariableName);
        Assert.DoesNotContain("from @case in query", expression, StringComparison.Ordinal);
        Assert.Contains("from @case in _context.PlusCases", expression, StringComparison.Ordinal);
        Assert.Contains("SingleOrDefaultAsync", expression, StringComparison.Ordinal);
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
}
