using EFQueryLens.Core.Contracts;
using EFQueryLens.Daemon;
using Microsoft.Extensions.Caching.Memory;

namespace EFQueryLens.Core.Tests.Daemon;

public class DaemonRuntimeTests
{
    [Fact]
    public void ComputeCacheKey_IsStable_ForEquivalentRequests()
    {
        var left = BuildRequest(
            additionalImports: ["System", "System.Linq"],
            aliases: new Dictionary<string, string> { ["X"] = "A.B", ["Y"] = "C.D" },
            staticTypes: ["System.Math", "System.String"],
            localTypes: new Dictionary<string, string> { ["b"] = "System.Int32", ["a"] = "System.String" },
            candidates: ["MyDb1", "MyDb2"]);

        var right = BuildRequest(
            additionalImports: ["System.Linq", "System"],
            aliases: new Dictionary<string, string> { ["Y"] = "C.D", ["X"] = "A.B" },
            staticTypes: ["System.String", "System.Math"],
            localTypes: new Dictionary<string, string> { ["a"] = "System.String", ["b"] = "System.Int32" },
            candidates: ["MyDb2", "MyDb1"]);

        var key1 = DaemonRuntime.ComputeCacheKey(left);
        var key2 = DaemonRuntime.ComputeCacheKey(right);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void ComputeCacheKey_Changes_WhenExpressionChanges()
    {
        var baseRequest = BuildRequest();
        var changed = baseRequest with { Expression = "db.Users" };

        var key1 = DaemonRuntime.ComputeCacheKey(baseRequest);
        var key2 = DaemonRuntime.ComputeCacheKey(changed);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void CacheStats_HitAndMiss_AreTracked()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var runtime = new DaemonRuntime(memoryCache);

        var miss = runtime.TryGetCached("k1", out _);
        Assert.False(miss);

        runtime.SetCached("k1", BuildTranslationResult());
        var hit = runtime.TryGetCached("k1", out var cached);

        Assert.True(hit);
        Assert.NotNull(cached);

        var (hits, misses) = runtime.ReadStats();
        Assert.Equal(1, hits);
        Assert.Equal(1, misses);
    }

    [Fact]
    public void ClearCache_RemovesEntries()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var runtime = new DaemonRuntime(memoryCache);

        runtime.SetCached("k1", BuildTranslationResult());
        Assert.True(runtime.IsCached("k1"));

        runtime.ClearCache();

        Assert.False(runtime.IsCached("k1"));
    }

    [Fact]
    public void Inflight_GetOrAdd_UsesSingleInstance_AndRemoveAllowsReplacement()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var runtime = new DaemonRuntime(memoryCache);

        var first = runtime.GetOrAddInflight(
            "k1",
            _ => new Lazy<Task<QueryTranslationResult>>(() => Task.FromResult(BuildTranslationResult())));

        var second = runtime.GetOrAddInflight(
            "k1",
            _ => new Lazy<Task<QueryTranslationResult>>(() => Task.FromResult(BuildTranslationResult())));

        Assert.Same(first, second);

        runtime.RemoveInflight("k1");

        var third = runtime.GetOrAddInflight(
            "k1",
            _ => new Lazy<Task<QueryTranslationResult>>(() => Task.FromResult(BuildTranslationResult())));

        Assert.NotSame(first, third);
    }

    [Fact]
    public void Touch_UpdatesLastActivity()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var runtime = new DaemonRuntime(memoryCache);

        var before = runtime.LastActivity;
        Thread.Sleep(5);
        runtime.Touch();

        Assert.True(runtime.LastActivity >= before);
    }

    [Fact]
    public void ValidateSnapshotConsistency_DoesNotThrow_ForMatchOrMismatch()
    {
        var matched = BuildRequest();
        var mismatched = matched with { AdditionalImports = ["System.Xml"] };

        DaemonRuntime.ValidateSnapshotConsistency(matched);
        DaemonRuntime.ValidateSnapshotConsistency(mismatched);
    }

    private static TranslationRequest BuildRequest(
        IReadOnlyList<string>? additionalImports = null,
        IReadOnlyDictionary<string, string>? aliases = null,
        IReadOnlyList<string>? staticTypes = null,
        IReadOnlyDictionary<string, string>? localTypes = null,
        IReadOnlyList<string>? candidates = null) =>
        new()
        {
            Expression = "db.Orders.Where(o => o.Id > 0)",
            AssemblyPath = "C:/app/MyApp.dll",
            DbContextTypeName = "MyDb",
            ContextVariableName = "db",
            UseAsyncRunner = true,
            AdditionalImports = additionalImports ?? ["System", "System.Linq"],
            UsingAliases = aliases ?? new Dictionary<string, string> { ["X"] = "A.B" },
            UsingStaticTypes = staticTypes ?? ["System.Math"],
            LocalVariableTypes = localTypes ?? new Dictionary<string, string> { ["a"] = "System.String" },
            DbContextResolution = new DbContextResolutionSnapshot
            {
                DeclaredTypeName = "MyDb",
                FactoryTypeName = "MyDb",
                FactoryCandidateTypeNames = candidates ?? ["MyDb"],
                ResolutionSource = "factory",
                Confidence = 0.8,
            },
            UsingContextSnapshot = new UsingContextSnapshot
            {
                Imports = additionalImports ?? ["System", "System.Linq"],
                Aliases = aliases ?? new Dictionary<string, string> { ["X"] = "A.B" },
                StaticTypes = staticTypes ?? ["System.Math"],
            },
            ExpressionMetadata = new ParsedExpressionMetadata
            {
                ExpressionType = "Invocation",
                SourceLine = 12,
                SourceCharacter = 4,
                Confidence = 0.9,
            },
        };

    private static QueryTranslationResult BuildTranslationResult() =>
        new()
        {
            Success = true,
            Sql = "SELECT 1",
            Metadata = new TranslationMetadata
            {
                DbContextType = "MyDb",
                EfCoreVersion = "9.0.0",
                ProviderName = "Provider",
                TranslationTime = TimeSpan.FromMilliseconds(1),
            },
        };
}
