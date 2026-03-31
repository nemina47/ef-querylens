namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    [Fact]
    public async Task Evaluate_ExplicitDbContextName_Resolves()
    {
        var result = await TranslateAsync("db.Users", dbContextTypeName: "MySqlAppDbContext");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Users", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_FullyQualifiedDbContextName_Resolves()
    {
        var result = await TranslateAsync("db.Users", dbContextTypeName: "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
    }

    [Fact]
    public async Task Evaluate_InterfaceDbContextName_Resolves()
    {
        var result = await TranslateAsync(
            "db.Users",
            dbContextTypeName: "SampleMySqlApp.Application.Abstractions.IMySqlAppDbContext");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("MySql", result.Metadata.ProviderName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_SecondarySampleDbContext_Resolves()
    {
        var result = await TranslateAsync("db.CustomerDirectory", dbContextTypeName: "MySqlReportingDbContext");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Customers", result.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
