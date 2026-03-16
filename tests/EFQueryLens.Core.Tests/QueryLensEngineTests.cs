namespace EFQueryLens.Core.Tests;

/// <summary>
/// Tests for <see cref="QueryLensEngine"/> — the top-level orchestrator.
///
/// SampleMySqlApp.dll is copied into an isolated SampleMySqlApp subfolder in the test
/// output directory by EFQueryLens.Core.Tests.csproj, so the assembly is available at runtime
/// via <see cref="GetSampleMySqlAppDll"/>.
/// </summary>
[Collection("AssemblyLoadContextIsolation")]
public class QueryLensEngineTests
{
    private const string DefaultMySqlDbContextType = "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext";

    private static string GetSampleMySqlAppDll()
    {
        var dir = Path.GetDirectoryName(typeof(QueryLensEngineTests).Assembly.Location)!;
        var dll = ResolveSampleDll(dir, "SampleMySqlApp.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException(
                $"SampleMySqlApp.dll not found in test output dir. Expected: {dll}");
        return dll;
    }

    private static string ResolveSampleDll(string testOutputDir, string dllName)
    {
        var isolated = Path.Combine(testOutputDir, "SampleMySqlApp", dllName);
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
                    DbContextTypeName = DefaultMySqlDbContextType,
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

    private static int GetPrivateCollectionCount(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found.");

        var value = field.GetValue(instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' is null.");

        var countProperty = value.GetType().GetProperty(
            "Count",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' does not expose Count.");

        return (int)(countProperty.GetValue(value)
            ?? throw new InvalidOperationException($"Field '{fieldName}' Count was null."));
    }

    // ── TranslateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_SimpleTable_ReturnsSuccessWithSql()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleMySqlAppDll(),
            Expression   = "db.Orders",
            DbContextTypeName = DefaultMySqlDbContextType,
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
            AssemblyPath = GetSampleMySqlAppDll(),
            Expression   = "db.Orders.Where(o => o.UserId == 5)",
            DbContextTypeName = DefaultMySqlDbContextType,
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
            AssemblyPath = GetSampleMySqlAppDll(),
            Expression   = "db.Orders.Where(o => o.UserId == 5).Include(o => o.Items)",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        // SampleMySqlApp uses SplitQuery globally, so Include is expected to emit
        // multiple commands and include the related OrderItems source.
        Assert.True(result.Commands.Count > 1,
            $"Expected split-query Include to emit multiple commands. Got {result.Commands.Count}.");
        Assert.Contains(result.Commands,
            c => c.Sql.Contains("OrderItems", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TranslateAsync_MultiLevelInclude_GeneratesValidSql()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleMySqlAppDll(),
            Expression   = "db.Orders.Include(o => o.Items).ThenInclude(i => i.Product)",
            DbContextTypeName = DefaultMySqlDbContextType,
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
            AssemblyPath = GetSampleMySqlAppDll(),
            Expression   = "db.Users.Select(u => new { u.Id, u.Email })",
            DbContextTypeName = DefaultMySqlDbContextType,
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
            AssemblyPath = GetSampleMySqlAppDll(),
            Expression   = "db.NonExistentTable.Where(x => x.Foo == 1)",
            DbContextTypeName = DefaultMySqlDbContextType,
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
            AssemblyPath = GetSampleMySqlAppDll(),
            Expression   = "42",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task TranslateAsync_SecondCall_UsesWarmCache_IsFaster()
    {
        await using var engine = CreateEngine();
        var dll = GetSampleMySqlAppDll();

        var r1 = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = dll,
            Expression   = "db.Orders",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        var r2 = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = dll,
            Expression   = "db.Products",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.True(r2.Success, r2.ErrorMessage);
        // Warm call (r2) should be strictly faster than cold compilation (r1).
        Assert.True(r2.Metadata.TranslationTime < r1.Metadata.TranslationTime,
            $"Expected warm ({r2.Metadata.TranslationTime.TotalMilliseconds:F0} ms) " +
            $"< cold ({r1.Metadata.TranslationTime.TotalMilliseconds:F0} ms)");
    }

    [Fact]
    public async Task TranslateAsync_CreateGate_IsPrunedAfterPoolWarmup()
    {
        await using var engine = CreateEngine();
        var dll = GetSampleMySqlAppDll();

        var first = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = dll,
            Expression = "db.Orders",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.True(first.Success, first.ErrorMessage);
        Assert.Equal(0, GetPrivateCollectionCount(engine, "_dbContextCreateGates"));

        var second = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = dll,
            Expression = "db.Products",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(0, GetPrivateCollectionCount(engine, "_dbContextCreateGates"));
        Assert.True(GetPrivateCollectionCount(engine, "_dbContextPool") >= 1);
    }

    [Fact]
    public async Task TranslateAsync_ConcurrentColdStart_MixedQueries_AllSucceedWithoutTypeMismatch()
    {
        var assemblyPath = GetSampleMySqlAppDll();
        var expressions = new[]
        {
            "db.Orders",
            "db.Orders.Where(o => o.UserId == 5)",
            "db.Orders.Where(o => o.UserId == 5).Include(o => o.Items)",
            "db.Orders.Include(o => o.Items).ThenInclude(i => i.Product)",
            "db.Users.Select(u => new { u.Id, u.Email })",
            "db.Categories",
            "db.Products",
            "db.OrderItems",
        };

        const int rounds = 3;
        const int parallelRequests = 20;

        for (var round = 0; round < rounds; round++)
        {
            await using var engine = CreateEngine();
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var tasks = Enumerable.Range(0, parallelRequests)
                .Select(async i =>
                {
                    await startGate.Task;
                    return await engine.TranslateAsync(new TranslationRequest
                    {
                        AssemblyPath = assemblyPath,
                        Expression = expressions[i % expressions.Length],
                        DbContextTypeName = DefaultMySqlDbContextType,
                    });
                })
                .ToArray();

            startGate.SetResult();
            var results = await Task.WhenAll(tasks);

            var failures = results
                .Where(r => !r.Success)
                .Select(r => r.ErrorMessage ?? "<null>")
                .ToArray();

            Assert.True(failures.Length == 0,
                $"Round {round + 1} had failures:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");

            Assert.DoesNotContain(results,
                r => (r.ErrorMessage ?? string.Empty).Contains("InvalidCastException", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TranslateAsync_Metadata_PopulatedCorrectly()
    {
        await using var engine = CreateEngine();
        var result = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = GetSampleMySqlAppDll(),
            Expression   = "db.Categories",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext", result.Metadata.DbContextType);
        Assert.Equal("Pomelo.EntityFrameworkCore.MySql", result.Metadata.ProviderName);
        Assert.NotEmpty(result.Metadata.EfCoreVersion);
        Assert.NotEqual("unknown", result.Metadata.EfCoreVersion);
    }

    // ── InspectModelAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task InspectModelAsync_ReturnsExpectedEntities()
    {
        await using var engine = CreateEngine();
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleMySqlAppDll());

        Assert.Equal("SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext", snapshot.DbContextType);
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
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleMySqlAppDll());

        var names = snapshot.Entities.Select(e => e.TableName).ToList();
        Assert.Contains("Orders", names);
        Assert.Contains("Users", names);
        Assert.Contains("Products", names);
    }

    [Fact]
    public async Task InspectModelAsync_OrderEntity_HasExpectedProperties()
    {
        await using var engine = CreateEngine();
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleMySqlAppDll());

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
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleMySqlAppDll());

        var order = snapshot.Entities.First(e => e.TableName == "Orders");
        var id    = order.Properties.FirstOrDefault(p => p.Name == "Id");
        Assert.NotNull(id);
        Assert.True(id.IsKey);
    }

    [Fact]
    public async Task InspectModelAsync_OrderEntity_HasNavigations()
    {
        await using var engine = CreateEngine();
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleMySqlAppDll());

        var order = snapshot.Entities.First(e => e.TableName == "Orders");
        Assert.NotEmpty(order.Navigations);
    }

    [Fact]
    public async Task InspectModelAsync_IncludesDbSetPropertyNames()
    {
        await using var engine = CreateEngine();
        var snapshot = await InspectModelWithRetryAsync(engine, GetSampleMySqlAppDll());

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
                AssemblyPath     = GetSampleMySqlAppDll(),
                Expression       = "db.Orders",
                ConnectionString = "Server=localhost",
            }));
    }
}
