using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Engine;
using System.Collections;

namespace EFQueryLens.Core.Tests;

public sealed class QueryLensEngineFixture : IAsyncLifetime
{
    public QueryLensEngine Engine { get; } = new();
    public string SampleMySqlAppDll { get; private set; } = string.Empty;

    public Task InitializeAsync()
    {
        SampleMySqlAppDll = QueryLensEngineTests.GetSampleMySqlAppDll();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await Engine.DisposeAsync();
}

/// <summary>
/// Tests for <see cref="QueryLensEngine"/> — the top-level orchestrator.
///
/// SampleMySqlApp.dll is copied into an isolated SampleMySqlApp subfolder in the test
/// output directory by EFQueryLens.Core.Tests.csproj, so the assembly is available at runtime
/// via <see cref="GetSampleMySqlAppDll"/>.
/// </summary>
[Collection("AssemblyLoadContextIsolation")]
public class QueryLensEngineTests : IClassFixture<QueryLensEngineFixture>
{
    private const string DefaultMySqlDbContextType = "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext";

    private readonly QueryLensEngine _engine;
    private readonly string _dll;

    public QueryLensEngineTests(QueryLensEngineFixture fixture)
    {
        _engine = fixture.Engine;
        _dll    = fixture.SampleMySqlAppDll;
    }

    internal static string GetSampleMySqlAppDll()
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

    private static int GetMaxDbContextPoolCreatedCount(object instance)
    {
        var field = instance.GetType().GetField(
            "_dbContextPool",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Field '_dbContextPool' not found.");

        var poolDictionary = field.GetValue(instance)
            ?? throw new InvalidOperationException("Field '_dbContextPool' is null.");

        var valuesProperty = poolDictionary.GetType().GetProperty(
            "Values",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException("Field '_dbContextPool' does not expose Values.");

        var values = valuesProperty.GetValue(poolDictionary) as IEnumerable
            ?? throw new InvalidOperationException("Field '_dbContextPool' Values is not enumerable.");

        var maxCreated = 0;
        foreach (var pool in values)
        {
            if (pool is null)
                continue;

            var createdCountField = pool.GetType().GetField(
                "CreatedCount",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                ?? throw new InvalidOperationException("PooledDbContextPool.CreatedCount field not found.");

            var createdCount = (int)(createdCountField.GetValue(pool)
                ?? throw new InvalidOperationException("PooledDbContextPool.CreatedCount is null."));

            if (createdCount > maxCreated)
            {
                maxCreated = createdCount;
            }
        }

        return maxCreated;
    }

    // ── TranslateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task TranslateAsync_SimpleTable_ReturnsSuccessWithSql()
    {
        var result = await _engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = _dll,
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
        var result = await _engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = _dll,
            Expression   = "db.Orders.Where(o => o.UserId == 5)",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("UserId", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranslateAsync_WithInclude_ContainsJoin()
    {
        var result = await _engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = _dll,
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
        var result = await _engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = _dll,
            Expression   = "db.Orders.Include(o => o.Items).ThenInclude(i => i.Product)",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
    }

    [Fact]
    public async Task TranslateAsync_SelectProjection_ReturnsSuccess()
    {
        var result = await _engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = _dll,
            Expression   = "db.Users.Select(u => new { u.Id, u.Email })",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
    }

    [Fact]
    public async Task TranslateAsync_InvalidExpression_ReturnsFalseSuccess()
    {
        var result = await _engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = _dll,
            Expression   = "db.NonExistentTable.Where(x => x.Foo == 1)",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task TranslateAsync_ExpressionReturningNonQueryable_ReturnsFalseSuccess()
    {
        var result = await _engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = _dll,
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
        var first = await _engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = _dll,
            Expression = "db.Orders",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.True(first.Success, first.ErrorMessage);
        Assert.Equal(0, GetPrivateCollectionCount(_engine, "_dbContextCreateGates"));

        var second = await _engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = _dll,
            Expression = "db.Products",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(0, GetPrivateCollectionCount(_engine, "_dbContextCreateGates"));
        Assert.True(GetPrivateCollectionCount(_engine, "_dbContextPool") >= 1);
    }

    [Fact]
    public async Task TranslateAsync_DbContextPoolSaturation_RespectsConfiguredPoolSize()
    {
        const string variableName = "QUERYLENS_DBCONTEXT_POOL_SIZE";
        var originalPoolSize = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, "2");

        try
        {
            await using var engine = CreateEngine();
            var dll = GetSampleMySqlAppDll();

            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, 12)
                .Select(async i =>
                {
                    await startGate.Task;
                    return await engine.TranslateAsync(new TranslationRequest
                    {
                        AssemblyPath = dll,
                        Expression = $"db.Orders.Where(o => o.UserId == {i % 5 + 1})",
                        DbContextTypeName = DefaultMySqlDbContextType,
                    });
                })
                .ToArray();

            startGate.SetResult();
            var results = await Task.WhenAll(requests);

            Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));

            var maxCreated = GetMaxDbContextPoolCreatedCount(engine);
            Assert.True(maxCreated <= 2, $"Expected pool creation <= 2, got {maxCreated}.");

            var nonReuseCount = results.Count(r =>
                !string.Equals(r.Metadata.CreationStrategy, "pooled-reuse", StringComparison.Ordinal));
            Assert.True(nonReuseCount <= 2, $"Expected <= 2 non-reuse leases, got {nonReuseCount}.");
            Assert.Contains(results, r => string.Equals(r.Metadata.CreationStrategy, "pooled-reuse", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalPoolSize);
        }
    }

    [Fact]
    public async Task TranslateAsync_DbContextPoolFairness_AllQueuedLeasesComplete()
    {
        const string variableName = "QUERYLENS_DBCONTEXT_POOL_SIZE";
        var originalPoolSize = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, "1");

        try
        {
            await using var engine = CreateEngine();
            var dll = GetSampleMySqlAppDll();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, 10)
                .Select(async i =>
                {
                    await startGate.Task.WaitAsync(timeoutCts.Token);
                    return await engine.TranslateAsync(new TranslationRequest
                    {
                        AssemblyPath = dll,
                        Expression = i % 2 == 0
                            ? "db.Orders.Where(o => o.Total > 0)"
                            : "db.Products.Where(p => p.Price > 0)",
                        DbContextTypeName = DefaultMySqlDbContextType,
                    }, timeoutCts.Token);
                })
                .ToArray();

            startGate.SetResult();
            var results = await Task.WhenAll(requests);

            Assert.Equal(10, results.Length);
            Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
            Assert.Contains(results, r => string.Equals(r.Metadata.CreationStrategy, "pooled-reuse", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalPoolSize);
        }
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
        var result = await _engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = _dll,
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
        var snapshot = await InspectModelWithRetryAsync(_engine, _dll);

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
        var snapshot = await InspectModelWithRetryAsync(_engine, _dll);

        var names = snapshot.Entities.Select(e => e.TableName).ToList();
        Assert.Contains("Orders", names);
        Assert.Contains("Users", names);
        Assert.Contains("Products", names);
    }

    [Fact]
    public async Task InspectModelAsync_OrderEntity_HasExpectedProperties()
    {
        var snapshot = await InspectModelWithRetryAsync(_engine, _dll);

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
        var snapshot = await InspectModelWithRetryAsync(_engine, _dll);

        var order = snapshot.Entities.First(e => e.TableName == "Orders");
        var id    = order.Properties.FirstOrDefault(p => p.Name == "Id");
        Assert.NotNull(id);
        Assert.True(id.IsKey);
    }

    [Fact]
    public async Task InspectModelAsync_OrderEntity_HasNavigations()
    {
        var snapshot = await InspectModelWithRetryAsync(_engine, _dll);

        var order = snapshot.Entities.First(e => e.TableName == "Orders");
        Assert.NotEmpty(order.Navigations);
    }

    [Fact]
    public async Task InspectModelAsync_IncludesDbSetPropertyNames()
    {
        var snapshot = await InspectModelWithRetryAsync(_engine, _dll);

        Assert.Contains("Orders", snapshot.DbSetProperties);
        Assert.Contains("Users", snapshot.DbSetProperties);
        Assert.Contains("Categories", snapshot.DbSetProperties);
    }

    // ── Guard tests (no real assembly execution needed) ───────────────────────

    [Fact]
    public async Task TranslateAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var engine = CreateEngine();
        await engine.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            engine.TranslateAsync(new TranslationRequest
            {
                AssemblyPath = _dll,
                Expression = "db.Orders",
            }));
    }

    [Fact]
    public async Task TranslateAsync_NullRequest_ThrowsArgumentNullException()
    {
        var engine = CreateEngine();
        await using var _ = engine;

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            engine.TranslateAsync(null!));
    }

    [Fact]
    public async Task InspectModelAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var engine = CreateEngine();
        await engine.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            engine.InspectModelAsync(new ModelInspectionRequest
            {
                AssemblyPath = _dll,
                DbContextTypeName = DefaultMySqlDbContextType,
            }));
    }

    [Fact]
    public async Task InspectModelAsync_NullRequest_ThrowsArgumentNullException()
    {
        var engine = CreateEngine();
        await using var _ = engine;

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            engine.InspectModelAsync(null!));
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        var engine = CreateEngine();
        await engine.DisposeAsync();
        // Second dispose must not throw
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task TranslateAsync_WithDebugEnabled_SucceedsAndLogsToStdErr()
    {
        const string varName = "QUERYLENS_DEBUG";
        var original = Environment.GetEnvironmentVariable(varName);
        Environment.SetEnvironmentVariable(varName, "true");

        try
        {
            await using var engine = CreateEngine();
            var result = await engine.TranslateAsync(new TranslationRequest
            {
                AssemblyPath = _dll,
                Expression = "db.Users",
                DbContextTypeName = DefaultMySqlDbContextType,
            });

            Assert.True(result.Success, result.ErrorMessage);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, original);
        }
    }

    [Fact]
    public async Task InspectModelAsync_WithDebugEnabled_Succeeds()
    {
        const string varName = "QUERYLENS_DEBUG";
        var original = Environment.GetEnvironmentVariable(varName);
        Environment.SetEnvironmentVariable(varName, "true");

        try
        {
            await using var engine = CreateEngine();
            var snapshot = await engine.InspectModelAsync(new ModelInspectionRequest
            {
                AssemblyPath = _dll,
                DbContextTypeName = DefaultMySqlDbContextType,
            });

            Assert.NotNull(snapshot);
            Assert.NotEmpty(snapshot.Entities);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, original);
        }
    }

}
