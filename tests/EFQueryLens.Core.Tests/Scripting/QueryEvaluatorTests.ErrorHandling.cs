using EFQueryLens.Core.Scripting.Evaluation;

namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    [Fact]
    public async Task Evaluate_InvalidExpression_ReturnsFailure()
    {
        var result = await TranslateAsync("this is not valid C#");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.True(
            result.ErrorMessage.Contains("error", StringComparison.OrdinalIgnoreCase)
            || result.ErrorMessage.Contains("No DbContext subclass found", StringComparison.OrdinalIgnoreCase),
            $"Unexpected error message: {result.ErrorMessage}");
    }

    [Fact]
    public async Task Evaluate_NonQueryableExpression_ReturnsFailure()
    {
        // A literal integer produces no SQL - capture records zero commands, so the
        // engine returns a hard failure. The old "did not return an IQueryable" guard
        // was removed; the new message reflects that no SQL was captured at all.
        var result = await TranslateAsync("42");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("no SQL commands", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_UnknownDbContextName_ReturnsFailure()
    {
        var result = await TranslateAsync("db.Orders", dbContextTypeName: "NoSuchContext");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("NoSuchContext", result.ErrorMessage);
    }

    [Fact]
    public async Task Evaluate_TopLevelServiceMethodInvocation_ReturnsClearUnsupportedMessage()
    {
        var result = await TranslateAsync("service.GetWorkflowByTypeAsync(workflowType, expression, ct)");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Top-level method invocations", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateDbContextInstance_WhenSelectedExecutableAssemblyDiffers_RejectsFactoriesFromOtherAssemblies()
    {
        var dbContextType = _alcCtx.FindDbContextType(
            "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext");
        var wrongExecutableAssemblyPath = Path.Combine(
            Path.GetDirectoryName(_alcCtx.AssemblyPath)!,
            "SomeOtherHost.dll");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            QueryEvaluator.CreateDbContextInstance(
                dbContextType,
                _alcCtx.LoadedAssemblies,
                wrongExecutableAssemblyPath));

        Assert.Contains("executable project", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("class library", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SomeOtherHost.dll", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
