// StubSynthesizerV2Tests.cs — unit tests for StubSynthesizer.BuildV2Stubs (v2-production-wiring-p9).
// Validates that the capture-plan-to-stubs adapter correctly converts entries and respects
// the Reject policy.
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Evaluation;
using Xunit;

namespace EFQueryLens.Core.Tests.Scripting;

public class StubSynthesizerV2Tests
{
    [Fact]
    public void BuildV2Stubs_NullPlan_ReturnsEmpty()
    {
        var stubs = StubSynthesizer.BuildV2Stubs(null!, string.Empty, "dbContext");

        Assert.Empty(stubs);
    }

    [Fact]
    public void BuildV2Stubs_EmptyEntries_ReturnsEmpty()
    {
        var plan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users",
            IsComplete = true,
            Entries = [],
        };

        var stubs = StubSynthesizer.BuildV2Stubs(plan, plan.ExecutableExpression, "dbContext");

        Assert.Empty(stubs);
    }

    [Fact]
    public void BuildV2Stubs_ReplayInitializerEntry_ReturnsOneStub()
    {
        var plan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users.Where(u => u.IsActive)",
            IsComplete = true,
            Entries =
            [
                new V2CapturePlanEntry
                {
                    Name = "user",
                    TypeName = "User",
                    CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer,
                    InitializerExpression = "new User { Id = 1 }",
                },
            ],
        };

        var stubs = StubSynthesizer.BuildV2Stubs(plan, plan.ExecutableExpression, "dbContext");

        Assert.Single(stubs);
        Assert.Contains("var user =", stubs[0]);
        Assert.Contains("new User { Id = 1 }", stubs[0]);
    }

    [Fact]
    public void BuildV2Stubs_UsePlaceholderEntry_ReturnsDefaultStub()
    {
        var plan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users.Take(limit)",
            IsComplete = true,
            Entries =
            [
                new V2CapturePlanEntry
                {
                    Name = "limit",
                    TypeName = "int",
                    CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                },
            ],
        };

        var stubs = StubSynthesizer.BuildV2Stubs(plan, plan.ExecutableExpression, "dbContext");

        Assert.Single(stubs);
        Assert.Contains("var limit =", stubs[0]);
        Assert.Contains("1", stubs[0]);
    }

    [Fact]
    public void BuildV2Stubs_RejectEntry_ExcludedFromStubs()
    {
        var plan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users",
            IsComplete = true,
            Entries =
            [
                new V2CapturePlanEntry
                {
                    Name = "rejected",
                    TypeName = "object",
                    CapturePolicy = LocalSymbolReplayPolicies.Reject,
                },
            ],
        };

        var stubs = StubSynthesizer.BuildV2Stubs(plan, plan.ExecutableExpression, "dbContext");

        Assert.Empty(stubs);
    }

    [Fact]
    public void BuildV2Stubs_MixedPolicies_OnlyNonRejectEntries()
    {
        var plan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users.Where(u => u.IsActive).Take(limit)",
            IsComplete = true,
            Entries =
            [
                new V2CapturePlanEntry
                {
                    Name = "u",
                    TypeName = "User",
                    CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer,
                    InitializerExpression = "new User()",
                },
                new V2CapturePlanEntry
                {
                    Name = "limit",
                    TypeName = "int",
                    CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                },
                new V2CapturePlanEntry
                {
                    Name = "rejected",
                    TypeName = "object",
                    CapturePolicy = LocalSymbolReplayPolicies.Reject,
                },
            ],
        };

        var stubs = StubSynthesizer.BuildV2Stubs(plan, plan.ExecutableExpression, "dbContext");

        Assert.Equal(2, stubs.Count);
        Assert.DoesNotContain(stubs, s => s.Contains("rejected"));
        Assert.Contains(stubs, s => s.Contains("var u ="));
        Assert.Contains(stubs, s => s.Contains("var limit ="));
    }

    [Fact]
    public void BuildV2Stubs_FactoryRootExpression_AddsSyntheticContextAliasStub()
    {
        var plan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "__qlFactoryContext.Rationales.AsNoTracking().OrderBy(x => x.Title).ToListAsync(ct)",
            IsComplete = true,
            Entries =
            [
                new V2CapturePlanEntry
                {
                    Name = "ct",
                    TypeName = "CancellationToken",
                    CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                },
            ],
        };

        var stubs = StubSynthesizer.BuildV2Stubs(plan, plan.ExecutableExpression, "Rationales");

        Assert.Contains(stubs, s => s.Contains("var __qlFactoryContext = Rationales;", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildV2Stubs_FactoryRootExpression_WithEmptyEntries_AddsSyntheticContextAliasStub()
    {
        var plan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "__qlFactoryContext.Rationales.AsNoTracking().OrderBy(x => x.Title).ToListAsync(ct)",
            IsComplete = true,
            Entries = [],
        };

        var stubs = StubSynthesizer.BuildV2Stubs(plan, plan.ExecutableExpression, "Rationales");

        Assert.Contains(stubs, s => s.Contains("var __qlFactoryContext = Rationales;", StringComparison.Ordinal));
    }
}
