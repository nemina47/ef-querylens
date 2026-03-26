using EFQueryLens.Core.Daemon;

namespace EFQueryLens.Core.Tests.Daemon;

public class QueryLensConfigTests
{
    [Fact]
    public void QueryLensConfig_DefaultContexts_IsEmpty()
    {
        var config = new QueryLensConfig();
        Assert.NotNull(config.Contexts);
        Assert.Empty(config.Contexts);
    }

    [Fact]
    public void QueryLensConfig_WithContexts_StoresProvided()
    {
        var ctx = new QueryLensContextConfig
        {
            Name = "MyCtx",
            Assembly = "MyApp.dll",
        };

        var config = new QueryLensConfig { Contexts = [ctx] };

        Assert.Single(config.Contexts);
        Assert.Same(ctx, config.Contexts[0]);
    }

    [Fact]
    public void QueryLensConfig_RecordEquality_EqualWhenSameContexts()
    {
        var a = new QueryLensConfig { Contexts = [] };
        var b = new QueryLensConfig { Contexts = [] };
        Assert.Equal(a, b);
    }

    [Fact]
    public void QueryLensContextConfig_RequiredProps_RoundTrip()
    {
        var ctx = new QueryLensContextConfig
        {
            Name = "Sales",
            Assembly = "Sales.dll",
        };

        Assert.Equal("Sales", ctx.Name);
        Assert.Equal("Sales.dll", ctx.Assembly);
    }

    [Fact]
    public void QueryLensContextConfig_OptionalProps_DefaultToNull()
    {
        var ctx = new QueryLensContextConfig
        {
            Name = "X",
            Assembly = "X.dll",
        };

        Assert.Null(ctx.DbContextType);
        Assert.Null(ctx.Provider);
    }

    [Fact]
    public void QueryLensContextConfig_AssemblySources_DefaultsToEmpty()
    {
        var ctx = new QueryLensContextConfig
        {
            Name = "X",
            Assembly = "X.dll",
        };

        Assert.NotNull(ctx.AssemblySources);
        Assert.Empty(ctx.AssemblySources);
    }

    [Fact]
    public void QueryLensContextConfig_AssemblySources_StoresProvided()
    {
        var ctx = new QueryLensContextConfig
        {
            Name = "X",
            Assembly = "X.dll",
            AssemblySources = ["a.dll", "b.dll"],
        };

        Assert.Equal(["a.dll", "b.dll"], ctx.AssemblySources);
    }

    [Fact]
    public void QueryLensContextConfig_AllOptionalProps_RoundTrip()
    {
        var ctx = new QueryLensContextConfig
        {
            Name = "Ctx",
            Assembly = "App.dll",
            DbContextType = "App.MyDbContext",
            Provider = "sqlserver",
            AssemblySources = ["dep.dll"],
        };

        Assert.Equal("App.MyDbContext", ctx.DbContextType);
        Assert.Equal("sqlserver", ctx.Provider);
        Assert.Equal(["dep.dll"], ctx.AssemblySources);
    }

    [Fact]
    public void QueryLensContextConfig_RecordEquality_EqualWhenSameValues()
    {
        var a = new QueryLensContextConfig { Name = "A", Assembly = "a.dll", Provider = "pg" };
        var b = new QueryLensContextConfig { Name = "A", Assembly = "a.dll", Provider = "pg" };
        Assert.Equal(a, b);
    }

    [Fact]
    public void QueryLensContextConfig_RecordEquality_NotEqualWhenDifferent()
    {
        var a = new QueryLensContextConfig { Name = "A", Assembly = "a.dll" };
        var b = new QueryLensContextConfig { Name = "B", Assembly = "a.dll" };
        Assert.NotEqual(a, b);
    }
}
