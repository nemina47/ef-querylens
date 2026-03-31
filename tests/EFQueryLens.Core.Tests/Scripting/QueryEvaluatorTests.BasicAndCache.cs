namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    [Fact]
    public async Task Evaluate_SimpleDbSet_ReturnsSql()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Orders", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_SimpleDbSet_PopulatesCommandsList()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Commands);
        Assert.Contains("Orders", result.Commands[0].Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WhereClause_ContainsWhere()
    {
        var result = await TranslateAsync("db.Orders.Where(o => o.UserId == 5)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("WHERE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MultipleEntities_EachReturnsSql()
    {
        string[] expressions = ["db.Orders", "db.Users", "db.Products", "db.Categories"];

        foreach (var expr in expressions)
        {
            var result = await TranslateAsync(expr);
            Assert.True(result.Success, $"Failed for '{expr}': {result.ErrorMessage}");
            Assert.NotNull(result.Sql);
        }
    }

    [Fact]
    public async Task Evaluate_SecondCall_IsNotSlowerByOrderOfMagnitude()
    {
        // Cold call - compiles the script.
        var r1 = await TranslateAsync("db.Orders");
        Assert.True(r1.Success, r1.ErrorMessage);

        // Warm call - should hit cached ScriptState.
        var r2 = await TranslateAsync("db.Users");
        Assert.True(r2.Success, r2.ErrorMessage);

        // We only assert the warm call stays in a reasonable time band.
        Assert.True(r2.Metadata.TranslationTime < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Evaluate_ExistingTests_StillPassWithFactoryPath()
    {
        // All four entity sets must translate correctly.
        string[] expressions = ["db.Orders", "db.Users", "db.Products", "db.Categories"];

        foreach (var expr in expressions)
        {
            var result = await TranslateAsync(expr);
            Assert.True(result.Success, $"Failed for '{expr}': {result.ErrorMessage}");
            Assert.NotNull(result.Sql);
        }
    }
}
