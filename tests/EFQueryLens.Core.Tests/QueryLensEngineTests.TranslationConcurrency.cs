using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Tests;

public partial class QueryLensEngineTests
{
    [Fact]
    public async Task TranslateAsync_SecondCall_UsesWarmCache_IsFaster()
    {
        await using var engine = CreateEngine();
        var dll = GetSampleMySqlAppDll();

        var r1 = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = dll,
            Expression = "db.Orders",
            DbContextTypeName = DefaultMySqlDbContextType,
        });

        var r2 = await engine.TranslateAsync(new TranslationRequest
        {
            AssemblyPath = dll,
            Expression = "db.Products",
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
}
