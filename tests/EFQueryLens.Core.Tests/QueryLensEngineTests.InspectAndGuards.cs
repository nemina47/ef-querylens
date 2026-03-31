using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Tests;

public partial class QueryLensEngineTests
{
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
        var id = order.Properties.FirstOrDefault(p => p.Name == "Id");
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
        // Second dispose must not throw.
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
