using EFQueryLens.Core.Scripting.Compilation;

namespace EFQueryLens.Core.Tests.Scripting;

public sealed class RunnerGeneratorTests
{
    [Fact]
    public void ValidateExpressionSyntax_ValidExpression_ReturnsNoErrors()
    {
        var errors = RunnerGenerator.ValidateExpressionSyntax("db.Orders.Where(o => o.Id > 0)");

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateExpressionSyntax_ValidStatementBlock_ReturnsNoErrors()
    {
        var input = """
            var query = db.Orders.Where(o => o.Id > 0);
            if (isActive)
            {
                query = query.Where(o => o.IsNotDeleted);
            }
            return query;
            """;

        var errors = RunnerGenerator.ValidateExpressionSyntax(input);

        Assert.Empty(errors);
    }

    [Fact]
    public void GenerateRunnerClass_StatementBlock_RewritesReturnToQueryAssignment()
    {
        var input = """
            var query = db.Orders.Where(o => o.Id > 0);
            return query;
            """;

        var source = RunnerGenerator.GenerateRunnerClass(
            "db",
            "My.Namespace.MyDbContext",
            input,
            [],
            useAsync: false);

        Assert.Contains("__query =", source, StringComparison.Ordinal);
        Assert.Contains("query", source, StringComparison.Ordinal);
        Assert.Contains("goto __ql_after_user_block;", source, StringComparison.Ordinal);
        Assert.Contains("__ql_after_user_block:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateRunnerClass_WithoutInterfaceProxyStub_DoesNotEmitDispatchProxyHelper()
    {
        var source = RunnerGenerator.GenerateRunnerClass(
            "db",
            "My.Namespace.MyDbContext",
            "db.Orders.Where(o => o.Id > 0)",
            [],
            useAsync: true);

        Assert.DoesNotContain("DispatchProxy", source, StringComparison.Ordinal);
        Assert.DoesNotContain("__CreateInterfaceProxy__", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateRunnerClass_WithInterfaceProxyStub_EmitsDispatchProxyHelper()
    {
        var source = RunnerGenerator.GenerateRunnerClass(
            "db",
            "My.Namespace.MyDbContext",
            "db.Orders.Where(o => o.Id > 0)",
            ["var clock = __CreateInterfaceProxy__<System.IFormatProvider>();"],
            useAsync: true);

        Assert.Contains("DispatchProxy", source, StringComparison.Ordinal);
        Assert.Contains("__CreateInterfaceProxy__", source, StringComparison.Ordinal);
    }
}
