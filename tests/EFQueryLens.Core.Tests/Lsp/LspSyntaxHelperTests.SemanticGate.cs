using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public partial class LspSyntaxHelperTests
{
    [Fact]
    public void PassesSemanticLinqGate_QueryableWhereSelect_ReturnsTrue()
    {
        var source = """
            using System.Collections.Generic;
            using System.Linq;
            
            public sealed class Demo
            {
                public void Run()
                {
                    IQueryable<int> numbers = new List<int> { 1, 2, 3 }.AsQueryable();
                    var projected = numbers.Where(n => n > 1).Select(n => n);
                }
            }
            """;

        var (line, character) = FindPosition(source, "Where");
        var allowed = LspSyntaxHelper.PassesSemanticLinqGate(source, line, character, targetAssemblyPath: null, out var reason);

        Assert.True(allowed, reason);
    }

    [Fact]
    public void PassesSemanticLinqGate_NonQueryableMathCall_ReturnsFalse()
    {
        var source = """
            using System;
            
            public sealed class Demo
            {
                public void Run()
                {
                    var value = Math.Max(10, 20);
                }
            }
            """;

        var (line, character) = FindPosition(source, "Max");
        var allowed = LspSyntaxHelper.PassesSemanticLinqGate(source, line, character, targetAssemblyPath: null, out _);

        Assert.False(allowed);
    }

    [Fact]
    public void PassesExtractedExpressionLinqShapeGate_QueryableChainShape_ReturnsTrue()
    {
        var expression = "dbContext.ApplicationDrafts.Where(x => x.IsNotDeleted).Where(d => d.ApplicationDetailsId == applicationId && d.Page == page).SingleOrDefaultAsync(cancellationToken: ct)";
        var allowed = LspSyntaxHelper.PassesExtractedExpressionLinqShapeGate(expression, "dbContext");
        Assert.True(allowed);
    }
}
