using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Contracts.Explain;

namespace EFQueryLens.Core.Tests.Contracts;

public class ExplainRequestTests
{
    [Fact]
    public void ExplainRequest_InheritsTranslationRequest()
    {
        Assert.True(typeof(ExplainRequest).IsAssignableTo(typeof(TranslationRequest)));
    }

    [Fact]
    public void ExplainRequest_UseAnalyze_DefaultsToTrue()
    {
        var req = new ExplainRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "App.dll",
            ConnectionString = "Server=localhost",
        };

        Assert.True(req.UseAnalyze);
    }

    [Fact]
    public void ExplainRequest_UseAnalyze_CanBeSetToFalse()
    {
        var req = new ExplainRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "App.dll",
            ConnectionString = "Server=localhost",
            UseAnalyze = false,
        };

        Assert.False(req.UseAnalyze);
    }

    [Fact]
    public void ExplainRequest_ConnectionString_RoundTrips()
    {
        const string cs = "Server=myhost;Database=mydb;User Id=sa;";
        var req = new ExplainRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "App.dll",
            ConnectionString = cs,
        };

        Assert.Equal(cs, req.ConnectionString);
    }

    [Fact]
    public void ExplainRequest_RecordEquality_EqualWhenSameReference()
    {
        var req = new ExplainRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "App.dll",
            ConnectionString = "cs",
        };

        // A record is always equal to itself.
        Assert.Equal(req, req);
    }

    [Fact]
    public void ExplainRequest_RecordEquality_NotEqualWhenConnectionStringDiffers()
    {
        var a = new ExplainRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "App.dll",
            ConnectionString = "cs1",
        };
        var b = new ExplainRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "App.dll",
            ConnectionString = "cs2",
        };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ExplainRequest_InheritsTranslationRequest_AdditionalImportsDefaultEmpty()
    {
        var req = new ExplainRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "App.dll",
            ConnectionString = "cs",
        };

        Assert.Empty(req.AdditionalImports);
    }

    [Fact]
    public void ExplainRequest_InheritsTranslationRequest_ContextVariableNameDefaultsToDb()
    {
        var req = new ExplainRequest
        {
            Expression = "db.Orders",
            AssemblyPath = "App.dll",
            ConnectionString = "cs",
        };

        Assert.Equal("db", req.ContextVariableName);
    }
}
