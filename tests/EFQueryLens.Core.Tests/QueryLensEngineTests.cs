namespace EFQueryLens.Core.Tests;

/// <summary>
/// Tests for <see cref="QueryLensEngine"/> — the top-level orchestrator.
///
/// SampleApp.dll is copied into an isolated SampleApp subfolder in the test
/// output directory by EFQueryLens.Core.Tests.csproj, so the assembly is available at runtime
/// via <see cref="GetSampleAppDll"/>.
/// </summary>
[Collection("AssemblyLoadContextIsolation")]
public class QueryLensEngineTests
{
    private static string GetSampleAppDll()
    {
        var dir = Path.GetDirectoryName(typeof(QueryLensEngineTests).Assembly.Location)!;
        var dll = ResolveSampleDll(dir, "SampleApp.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException(
                $"SampleApp.dll not found in test output dir. Expected: {dll}");
        return dll;
    }

    private static string ResolveSampleDll(string testOutputDir, string dllName)
    {
        var isolated = Path.Combine(testOutputDir, "SampleApp", dllName);
        if (File.Exists(isolated))
            return isolated;

        // Backward compatibility for older builds that copied files into root.
        return Path.Combine(testOutputDir, dllName);
    }

    private static QueryLensEngine CreateEngine() => new();

    private static async Task<ModelSnapshot> InspectModelWithRetryAsync(QueryLensEngine engine, string assemblyPath)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await engine.InspectModelAsync(new ModelInspectionRequest
                {
                    AssemblyPath = assemblyPath,
                });
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientAlcUnload(ex))
            {
                await Task.Delay(100 * attempt);
            }
        }

        throw new InvalidOperationException("InspectModelAsync failed after transient-retry attempts.");
    }

    private static bool IsTransientAlcUnload(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException ioe
                && ioe.Message.Contains("AssemblyLoadContext is unloading", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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
    public async Task InspectModelAsync_ReturnsExpectedEntities()
    {
        await using var engine = CreateEngine();
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleAppDll());

        Assert.Equal("SampleApp.AppDbContext", snapshot.DbContextType);
        Assert.True(snapshot.Entities.Count >= 5);

        var tableNames = snapshot.Entities.Select(e => e.TableName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Orders", tableNames);
        Assert.Contains("Users", tableNames);
        Assert.Contains("Products", tableNames);
        Assert.Contains("Categories", tableNames);
        Assert.Contains("OrderItems", tableNames);
    }

    [Fact]
    public async Task InspectModelAsync_ContainsExpectedTableNames()
    {
        await using var engine = CreateEngine();
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleAppDll());

        var names = snapshot.Entities.Select(e => e.TableName).ToList();
        Assert.Contains("Orders", names);
        Assert.Contains("Users", names);
        Assert.Contains("Products", names);
    }

    [Fact]
    public async Task InspectModelAsync_OrderEntity_HasExpectedProperties()
    {
        await using var engine = CreateEngine();
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleAppDll());

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
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleAppDll());

        var order = snapshot.Entities.First(e => e.TableName == "Orders");
        var id    = order.Properties.FirstOrDefault(p => p.Name == "Id");
        Assert.NotNull(id);
        Assert.True(id.IsKey);
    }

    [Fact]
    public async Task InspectModelAsync_OrderEntity_HasNavigations()
    {
        await using var engine = CreateEngine();
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleAppDll());

        var order = snapshot.Entities.First(e => e.TableName == "Orders");
        Assert.NotEmpty(order.Navigations);
    }

    [Fact]
    public async Task InspectModelAsync_IncludesDbSetPropertyNames()
    {
        await using var engine = CreateEngine();
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleAppDll());

        Assert.Contains("Orders", snapshot.DbSetProperties);
        Assert.Contains("Users", snapshot.DbSetProperties);
        Assert.Contains("Categories", snapshot.DbSetProperties);
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
