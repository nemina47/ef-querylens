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

    // Duck-typed: has CreateOfflineContext() without implementing the interface directly
    private sealed class DuckTypedFactory
    {
        public FakeContextA CreateOfflineContext() => new();
    }
}
