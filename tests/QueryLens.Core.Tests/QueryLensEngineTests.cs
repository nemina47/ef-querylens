namespace QueryLens.Core.Tests;

/// <summary>
/// Tests for <see cref="QueryLensEngine"/> — the top-level orchestrator.
///
/// SampleApp.dll is copied into the test output directory by the MSBuild target
/// in QueryLens.Core.Tests.csproj, so the assembly is available at runtime
/// via <see cref="GetSampleAppDll"/>.
/// </summary>
[Collection("AssemblyLoadContextIsolation")]
public class QueryLensEngineTests
{
    private static string GetSampleAppDll()
    {
        var dir = Path.GetDirectoryName(typeof(QueryLensEngineTests).Assembly.Location)!;
        var dll = Path.Combine(dir, "SampleApp.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException(
                $"SampleApp.dll not found in test output dir. Expected: {dll}");
        return dll;
    }

    private static QueryLensEngine CreateEngine() => new();

    // ── TranslateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_SimpleTable_ReturnsSuccessWithSql()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleAppDll(),
            Expression   = "db.Orders",
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Orders", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranslateAsync_WhereClause_ContainsFilterColumn()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleAppDll(),
            Expression   = "db.Orders.Where(o => o.UserId == 5)",
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("UserId", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranslateAsync_WithInclude_ContainsJoin()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleAppDll(),
            Expression   = "db.Orders.Where(o => o.UserId == 5).Include(o => o.Items)",
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("JOIN", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranslateAsync_MultiLevelInclude_GeneratesValidSql()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleAppDll(),
            Expression   = "db.Orders.Include(o => o.Items).ThenInclude(i => i.Product)",
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
    }

    [Fact]
    public async Task TranslateAsync_SelectProjection_ReturnsSuccess()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleAppDll(),
            Expression   = "db.Users.Select(u => new { u.Id, u.Email })",
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
    }

    [Fact]
    public async Task TranslateAsync_InvalidExpression_ReturnsFalseSuccess()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleAppDll(),
            Expression   = "db.NonExistentTable.Where(x => x.Foo == 1)",
        });

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task TranslateAsync_ExpressionReturningNonQueryable_ReturnsFalseSuccess()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleAppDll(),
            Expression   = "42",
        });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task TranslateAsync_SecondCall_UsesWarmCache_IsFaster()
    {
        await using var engine = CreateEngine();
        var dll = GetSampleAppDll();

        var r1 = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = dll,
            Expression   = "db.Orders",
        });

        var r2 = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = dll,
            Expression   = "db.Products",
        });

        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.True(r2.Success, r2.ErrorMessage);
        // Warm call (r2) should be strictly faster than cold compilation (r1).
        Assert.True(r2.Metadata.TranslationTime < r1.Metadata.TranslationTime,
            $"Expected warm ({r2.Metadata.TranslationTime.TotalMilliseconds:F0} ms) " +
            $"< cold ({r1.Metadata.TranslationTime.TotalMilliseconds:F0} ms)");
    }

    [Fact]
    public async Task TranslateAsync_Metadata_PopulatedCorrectly()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleAppDll(),
            Expression   = "db.Categories",
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("SampleApp.AppDbContext", result.Metadata.DbContextType);
        Assert.Equal("Pomelo.EntityFrameworkCore.MySql", result.Metadata.ProviderName);
        Assert.NotEmpty(result.Metadata.EfCoreVersion);
        Assert.NotEqual("unknown", result.Metadata.EfCoreVersion);
    }

    // ── InspectModelAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task InspectModelAsync_ReturnsAllFiveEntities()
    {
        await using var engine = CreateEngine();
        var snapshot = await engine.InspectModelAsync(new ModelInspectionRequest
        {
            AssemblyPath = GetSampleAppDll(),
        });

        Assert.Equal("SampleApp.AppDbContext", snapshot.DbContextType);
        Assert.Equal(5, snapshot.Entities.Count);
    }

    [Fact]
    public async Task InspectModelAsync_ContainsExpectedTableNames()
    {
        await using var engine = CreateEngine();
        var snapshot = await engine.InspectModelAsync(new ModelInspectionRequest
        {
            AssemblyPath = GetSampleAppDll(),
        });

        var names = snapshot.Entities.Select(e => e.TableName).ToList();
        Assert.Contains("Orders", names);
        Assert.Contains("Users", names);
        Assert.Contains("Products", names);
    }

    [Fact]
    public async Task InspectModelAsync_OrderEntity_HasExpectedProperties()
    {
        await using var engine = CreateEngine();
        var snapshot = await engine.InspectModelAsync(new ModelInspectionRequest
        {
            AssemblyPath = GetSampleAppDll(),
        });

        var order = snapshot.Entities.FirstOrDefault(e => e.TableName == "Orders");
        Assert.NotNull(order);

        var propNames = order.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Id", propNames);
        Assert.Contains("UserId", propNames);
        Assert.Contains("Total", propNames);
    }

    [Fact]
    public async Task InspectModelAsync_OrderEntity_IdIsKey()
    {
        await using var engine = CreateEngine();
        var snapshot = await engine.InspectModelAsync(new ModelInspectionRequest
        {
            AssemblyPath = GetSampleAppDll(),
        });

        var order = snapshot.Entities.First(e => e.TableName == "Orders");
        var id    = order.Properties.FirstOrDefault(p => p.Name == "Id");
        Assert.NotNull(id);
        Assert.True(id.IsKey);
    }

    [Fact]
    public async Task InspectModelAsync_OrderEntity_HasNavigations()
    {
        await using var engine = CreateEngine();
        var snapshot = await engine.InspectModelAsync(new ModelInspectionRequest
        {
            AssemblyPath = GetSampleAppDll(),
        });

        var order = snapshot.Entities.First(e => e.TableName == "Orders");
        Assert.NotEmpty(order.Navigations);
    }

    // ── ExplainAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExplainAsync_ThrowsNotImplemented()
    {
        await using var engine = CreateEngine();
        await Assert.ThrowsAsync<NotImplementedException>(() =>
            engine.ExplainAsync(new ExplainRequest
            {
                AssemblyPath     = GetSampleAppDll(),
                Expression       = "db.Orders",
                ConnectionString = "Server=localhost",
            }));
    }
}
