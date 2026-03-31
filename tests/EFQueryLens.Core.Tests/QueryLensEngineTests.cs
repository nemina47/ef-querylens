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
public partial class QueryLensEngineTests : IClassFixture<QueryLensEngineFixture>
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

}
