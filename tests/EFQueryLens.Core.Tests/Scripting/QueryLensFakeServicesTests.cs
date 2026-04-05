using EFQueryLens.Core.Scripting.Evaluation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EFQueryLens.Core.Tests.Scripting;

/// <summary>
/// Regression tests for fake IConfiguration used during offline DbContext creation.
/// Ensures EF named connection strings resolve through ConnectionStrings section lookups.
/// </summary>
public class QueryLensFakeServicesTests
{
    [Fact]
    public void Configuration_GetSectionConnectionStringsIndexer_ReturnsDummyConnectionString()
    {
        var configuration = new QueryLensFakeServices.Configuration();

        var value = configuration.GetSection("ConnectionStrings")["MainConnection"];

        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.Contains("Database=__querylens__", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_GetConnectionString_ReturnsDummyConnectionStringForAnyName()
    {
        var configuration = new QueryLensFakeServices.Configuration();

        var main = configuration.GetConnectionString("MainConnection");
        var custom = configuration.GetConnectionString("AnotherConnectionName");

        Assert.False(string.IsNullOrWhiteSpace(main));
        Assert.False(string.IsNullOrWhiteSpace(custom));
        Assert.Equal(main, custom);
    }
}
