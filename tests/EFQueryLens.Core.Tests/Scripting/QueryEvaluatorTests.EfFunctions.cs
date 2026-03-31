namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    [Fact]
    public async Task Evaluate_EfFunctionsLike_WithCapturedPatternLocal_ReturnsSql()
    {
        // Regression: pattern variable stubbed as 'object' caused CS1503
        // ("cannot convert from 'object' to 'string?") for EF.Functions.Like.
        // After the fix, CS1503 is a soft error; the re-stub handler detects the
        // expected type from the diagnostic and replaces 'object' with 'string'.
        const string expression =
            "db.Customers" +
            "    .Where(c => c.IsNotDeleted)" +
            "    .Where(c => EF.Functions.Like(c.Name, pattern))";

        var result = await TranslateAsync(expression);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("CS1503", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIKE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_EfFunctionsLike_WithEscapeCharacterOverload_ReturnsSql()
    {
        // EF.Functions.Like has a 3-argument overload (matchExpression, pattern, escapeCharacter).
        // All three captured locals must be re-stubbed with 'string' when CS1503 fires.
        const string expression =
            "db.Customers" +
            "    .Where(c => c.IsNotDeleted)" +
            "    .Where(c => EF.Functions.Like(c.Name, pattern, escape))";

        var result = await TranslateAsync(expression);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("LIKE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
