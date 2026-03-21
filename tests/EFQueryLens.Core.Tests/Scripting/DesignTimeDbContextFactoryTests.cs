using System.Reflection;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting;
using EFQueryLens.Core.Scripting.DesignTime;

namespace EFQueryLens.Core.Tests.Scripting;

public class DesignTimeDbContextFactoryTests
{
    [Fact]
    public void TryCreateQueryLensFactory_WhenImplemented_ReturnsContext()
    {
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextA),
            [typeof(SingleContextFactory).Assembly],
            out var failureReason);

        Assert.NotNull(result);
        Assert.IsType<FakeContextA>(result);
        Assert.True(string.IsNullOrWhiteSpace(failureReason));
    }

    [Fact]
    public void TryCreateQueryLensFactory_MultiContextExplicitFactory_ReturnsRequestedContextA()
    {
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextA),
            [typeof(MultiContextFactory).Assembly],
            out var failureReason);

        Assert.NotNull(result);
        Assert.IsType<FakeContextA>(result);
        Assert.True(string.IsNullOrWhiteSpace(failureReason));
    }

    [Fact]
    public void TryCreateQueryLensFactory_MultiContextExplicitFactory_ReturnsRequestedContextB()
    {
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextB),
            [typeof(MultiContextFactory).Assembly],
            out var failureReason);

        Assert.NotNull(result);
        Assert.IsType<FakeContextB>(result);
        Assert.True(string.IsNullOrWhiteSpace(failureReason));
    }

    [Fact]
    public void TryCreateQueryLensFactory_WhenNotImplemented_ReturnsNull()
    {
        // FakeContextUnregistered has no factory — not via interface, not via duck-typing.
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextUnregistered),
            [typeof(DesignTimeDbContextFactoryTests).Assembly],
            out _);

        Assert.Null(result);
    }

    [Fact]
    public void TryCreateQueryLensFactory_DuckTypedFactory_ReturnsContext()
    {
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextA),
            [typeof(DuckTypedFactory).Assembly],
            out var failureReason);

        Assert.NotNull(result);
        Assert.IsType<FakeContextA>(result);
        Assert.True(string.IsNullOrWhiteSpace(failureReason));
    }

    private sealed class FakeContextA;
    private sealed class FakeContextB;
    private sealed class FakeContextC; // only handled by ThrowingFactoryC
    private sealed class FakeContextUnregistered; // intentionally has no factory

    private sealed class SingleContextFactory : IQueryLensDbContextFactory<FakeContextA>
    {
        public FakeContextA CreateOfflineContext() => new();
    }

    private sealed class MultiContextFactory :
        IQueryLensDbContextFactory<FakeContextA>,
        IQueryLensDbContextFactory<FakeContextB>
    {
        public FakeContextA CreateOfflineContext() => new();
        FakeContextB IQueryLensDbContextFactory<FakeContextB>.CreateOfflineContext() => new();
    }

    [Fact]
    public void TryCreateQueryLensFactory_WithRequiredAssemblyPath_WhenMatches_ReturnsContext()
    {
        // requiredFactoryAssemblyPath that matches the test assembly itself
        var thisAssembly = typeof(DesignTimeDbContextFactoryTests).Assembly;
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextA),
            [thisAssembly],
            requiredFactoryAssemblyPath: thisAssembly.Location,
            out var failureReason);

        Assert.NotNull(result);
        Assert.IsType<FakeContextA>(result);
        Assert.True(string.IsNullOrWhiteSpace(failureReason));
    }

    [Fact]
    public void TryCreateQueryLensFactory_WithRequiredAssemblyPath_WhenMismatches_ReturnsNullWithReason()
    {
        // requiredFactoryAssemblyPath set to a path that does NOT match the test assembly
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextA),
            [typeof(SingleContextFactory).Assembly],
            requiredFactoryAssemblyPath: @"C:\does\not\exist\other.dll",
            out var failureReason);

        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(failureReason));
    }

    [Fact]
    public void TryCreateQueryLensFactory_WhenCreateOfflineContextThrows_ReturnsNullWithReason()
    {
        // FakeContextC has no other factory — only ThrowingFactoryC handles it.
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextC),
            [typeof(ThrowingFactoryC).Assembly],
            out var failureReason);

        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(failureReason));
        Assert.Contains("ThrowingFactoryC", failureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryCreateQueryLensFactory_WhenNoFactoryExists_AndRequiredPathNull_ReturnsNull()
    {
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextUnregistered),
            [typeof(DesignTimeDbContextFactoryTests).Assembly],
            requiredFactoryAssemblyPath: null,
            out _);

        Assert.Null(result);
    }

    [Fact]
    public void TryCreateQueryLensFactory_TwoArgOverload_WhenImplemented_ReturnsContext()
    {
        // Tests the (Type, IEnumerable<Assembly>) overload (no out, no requiredPath)
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextA),
            [typeof(SingleContextFactory).Assembly]);

        Assert.NotNull(result);
        Assert.IsType<FakeContextA>(result);
    }

    [Fact]
    public void TryCreateQueryLensFactory_ThreeArgWithPathOverload_WhenMatchingPath_ReturnsContext()
    {
        // Tests the (Type, IEnumerable<Assembly>, string?) overload (no out param)
        var thisAssembly = typeof(DesignTimeDbContextFactoryTests).Assembly;
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextA),
            [thisAssembly],
            requiredFactoryAssemblyPath: thisAssembly.Location);

        Assert.NotNull(result);
        Assert.IsType<FakeContextA>(result);
    }

    [Fact]
    public void TryCreateQueryLensFactory_WithEmptyAssemblyList_ReturnsNull()
    {
        var result = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
            typeof(FakeContextA),
            Enumerable.Empty<Assembly>(),
            out var failureReason);

        Assert.Null(result);
        Assert.True(string.IsNullOrWhiteSpace(failureReason));
    }

    // Duck-typed: has CreateOfflineContext() without implementing the interface directly
    private sealed class DuckTypedFactory
    {
        public FakeContextA CreateOfflineContext() => new();
    }

    private sealed class ThrowingFactoryC : IQueryLensDbContextFactory<FakeContextC>
    {
        public FakeContextC CreateOfflineContext() =>
            throw new InvalidOperationException("CreateOfflineContext intentionally thrown by ThrowingFactoryC");
    }
}
