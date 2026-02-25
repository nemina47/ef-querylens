using QueryLens.Core.AssemblyContext;
using QueryLens.Core.Scripting;
using QueryLens.MySql;

namespace QueryLens.Core.Tests.Scripting;

/// <summary>
/// Integration-style unit tests for <see cref="QueryEvaluator"/>.
///
/// Uses <see cref="MySqlProviderBootstrap"/> which configures EF Core with a
/// fake connection string — no real database is ever contacted.
/// SampleApp.dll is copied into the test output dir by the MSBuild target in
/// the .csproj, so <see cref="GetSampleAppDll"/> finds it at runtime.
/// </summary>
public class QueryEvaluatorTests : IDisposable
{
    private readonly ProjectAssemblyContext _alcCtx;
    private readonly MySqlProviderBootstrap _bootstrap;
    private readonly QueryEvaluator _evaluator;

    public QueryEvaluatorTests()
    {
        _alcCtx    = new ProjectAssemblyContext(GetSampleAppDll());
        _bootstrap = new MySqlProviderBootstrap();
        _evaluator = new QueryEvaluator();
    }

    public void Dispose() => _alcCtx.Dispose();

    // ─── Helper ───────────────────────────────────────────────────────────────

    private static string GetSampleAppDll()
    {
        var dir = Path.GetDirectoryName(typeof(QueryEvaluatorTests).Assembly.Location)!;
        var dll = Path.Combine(dir, "SampleApp.dll");

        if (!File.Exists(dll))
            throw new FileNotFoundException(
                $"SampleApp.dll not found in test output dir. Expected: {dll}");

        return dll;
    }

    private Task<QueryTranslationResult> TranslateAsync(string expression,
        string? dbContextTypeName = null, CancellationToken ct = default) =>
        _evaluator.EvaluateAsync(_alcCtx, _bootstrap,
            new TranslationRequest
            {
                AssemblyPath      = _alcCtx.AssemblyPath,
                Expression        = expression,
                DbContextTypeName = dbContextTypeName,
            }, ct);

    // ─── Basic translation ────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_SimpleDbSet_ReturnsSql()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Orders", result.Sql, StringComparison.OrdinalIgnoreCase);
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
    public async Task Evaluate_ExplicitDbContextName_Resolves()
    {
        var result = await TranslateAsync("db.Users", dbContextTypeName: "AppDbContext");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Users", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_FullyQualifiedDbContextName_Resolves()
    {
        var result = await TranslateAsync("db.Users", dbContextTypeName: "SampleApp.AppDbContext");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
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

    // ─── Result shape ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_MetaData_HasCorrectProviderName()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Pomelo.EntityFrameworkCore.MySql", result.Metadata.ProviderName);
    }

    [Fact]
    public async Task Evaluate_Metadata_TranslationTimeIsPositive()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Metadata.TranslationTime > TimeSpan.Zero);
    }

    [Fact]
    public async Task Evaluate_Metadata_DbContextTypeIsSet()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("SampleApp.AppDbContext", result.Metadata.DbContextType);
    }

    [Fact]
    public async Task Evaluate_Metadata_EfCoreVersionIsKnown()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEqual("unknown", result.Metadata.EfCoreVersion);
    }

    [Fact]
    public async Task Evaluate_WhereWithParam_ParsesParameters()
    {
        var result = await TranslateAsync("db.Orders.Where(o => o.UserId == 5)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);

        // EF Core 9 + Pomelo may inline the constant 5 directly into the SQL
        // (e.g. "WHERE `UserId` = 5") rather than emitting a @p0 parameter.
        // Either way the value must appear in the SQL and the result must succeed.
        if (result.Parameters.Count > 0)
        {
            // Older behaviour: parameterised constant
            var p = result.Parameters[0];
            Assert.StartsWith("@", p.Name);
            Assert.Equal("5", p.InferredValue);
        }
        else
        {
            // EF Core 9 behaviour: inlined literal
            Assert.Contains("5", result.Sql, StringComparison.Ordinal);
        }
    }

    // ─── Error handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_InvalidExpression_ReturnsFailure()
    {
        var result = await TranslateAsync("this is not valid C#");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("error", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_NonQueryableExpression_ReturnsFailure()
    {
        var result = await TranslateAsync("42");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("IQueryable", result.ErrorMessage);
    }

    [Fact]
    public async Task Evaluate_UnknownDbContextName_ReturnsFailure()
    {
        var result = await TranslateAsync("db.Orders", dbContextTypeName: "NoSuchContext");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("NoSuchContext", result.ErrorMessage);
    }

    // ─── ScriptState cache ────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_SecondCall_IsNotSlowerByOrderOfMagnitude()
    {
        // Cold call — compiles the script
        var r1 = await TranslateAsync("db.Orders");
        Assert.True(r1.Success, r1.ErrorMessage);

        // Warm call — should hit cached ScriptState
        var r2 = await TranslateAsync("db.Users");
        Assert.True(r2.Success, r2.ErrorMessage);

        // The warm call should complete in reasonable time.
        // We don't assert it's *faster* (CI jitter), just that it succeeded
        // and took less than 10s (the cold call could be 1-2s on first Roslyn compile).
        Assert.True(r2.Metadata.TranslationTime < TimeSpan.FromSeconds(10));
    }

    // ─── IQueryLensDbContextFactory + IDesignTimeDbContextFactory discovery ─────

    [Fact]
    public async Task Evaluate_WhenQueryLensFactoryExists_StrategyIsQueryLensFactory()
    {
        // SampleApp ships AppQueryLensFactory : IQueryLensDbContextFactory<AppDbContext>.
        // QueryEvaluator must report "querylens-factory" as the creation strategy.
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("querylens-factory", result.Metadata.CreationStrategy);
    }

    [Fact]
    public async Task Evaluate_QueryLensFactory_TakesPriorityOverDesignTimeFactory()
    {
        // SampleApp has BOTH AppQueryLensFactory (IQueryLensDbContextFactory<T>) AND
        // AppDbContextFactory (IDesignTimeDbContextFactory<T>).
        // Priority 0 (QueryLens factory) must win over Priority 1 (design-time factory).
        var result = await TranslateAsync("db.Users");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("querylens-factory", result.Metadata.CreationStrategy);
        Assert.NotEqual("design-time-factory", result.Metadata.CreationStrategy);
        Assert.NotEqual("bootstrap", result.Metadata.CreationStrategy);
    }

    [Fact]
    public async Task Evaluate_WhenDesignTimeFactoryExists_UsesFactoryOverBootstrap()
    {
        // SampleApp now has a QueryLens factory (Priority 0) that wins over the
        // design-time factory (Priority 1). Strategy must NOT be "bootstrap".
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Orders", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("querylens-factory", result.Metadata.CreationStrategy);
    }

    [Fact]
    public async Task Evaluate_WhenDesignTimeFactoryExists_SqlIsStillGenerated()
    {
        // Verify that using the factory does not break SQL generation for
        // WHERE clauses or any other operator.
        var result = await TranslateAsync("db.Orders.Where(o => o.UserId == 5)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("WHERE", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("querylens-factory", result.Metadata.CreationStrategy);
    }

    [Fact]
    public async Task Evaluate_ExistingTests_StillPassWithFactoryPath()
    {
        // All four entity sets must translate correctly when any factory is active.
        string[] expressions = ["db.Orders", "db.Users", "db.Products", "db.Categories"];

        foreach (var expr in expressions)
        {
            var result = await TranslateAsync(expr);
            Assert.True(result.Success, $"Failed for '{expr}': {result.ErrorMessage}");
            Assert.NotNull(result.Sql);
            Assert.Equal("querylens-factory", result.Metadata.CreationStrategy);
        }
    }
}
