using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;
using Xunit;

namespace EFQueryLens.Core.Tests.Scripting;

/// <summary>
/// Unit tests for RunnerGenerator V2 capture-plan support (Slice 3b step 3).
/// Tests v2-aware initialization code generation from capture plans.
/// </summary>
public class RunnerGeneratorV2Tests
{
    [Fact]
    public void BuildV2CapturePlanInitialization_NullPlan_ReturnsEmpty()
    {
        var statements = RunnerGenerator.BuildV2CapturePlanInitialization(null);

        Assert.Empty(statements);
    }

    [Fact]
    public void BuildV2CapturePlanInitialization_EmptyEntries_ReturnsEmpty()
    {
        var capturePlan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users",
            IsComplete = true,
            Entries = Array.Empty<V2CapturePlanEntry>(),
        };

        var statements = RunnerGenerator.BuildV2CapturePlanInitialization(capturePlan);

        Assert.Empty(statements);
    }

    [Fact]
    public void BuildV2CapturePlanInitialization_SingleReplayEntry_GeneratesStatement()
    {
        var capturePlan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users.Where(u => u.Id > 0)",
            IsComplete = true,
            Entries = new[]
            {
                new V2CapturePlanEntry
                {
                    Name = "u",
                    TypeName = "User",
                    CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer,
                    InitializerExpression = "new User { Id = 1 }",
                },
            },
        };

        var statements = RunnerGenerator.BuildV2CapturePlanInitialization(capturePlan);

        Assert.Single(statements);
        var code = statements[0].ToString();
        Assert.Contains("u", code);
        Assert.Contains("User", code);
    }

    [Fact]
    public void BuildV2CapturePlanInitialization_MultipleEntries_GeneratesMultipleStatements()
    {
        var capturePlan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users.Where(u => u.Status == status).Take(limit)",
            IsComplete = true,
            Entries = new[]
            {
                new V2CapturePlanEntry
                {
                    Name = "u",
                    TypeName = "User",
                    CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer,
                    InitializerExpression = "new User()",
                },
                new V2CapturePlanEntry
                {
                    Name = "status",
                    TypeName = "UserStatus",
                    CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                },
                new V2CapturePlanEntry
                {
                    Name = "limit",
                    TypeName = "int",
                    CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer,
                    InitializerExpression = "10",
                },
            },
        };

        var statements = RunnerGenerator.BuildV2CapturePlanInitialization(capturePlan);

        // Should generate 2 statements (Reject policy skipped)
        Assert.Equal(3, statements.Count);
    }

    [Fact]
    public void BuildV2CapturePlanInitialization_WithRejectPolicy_SkipsEntry()
    {
        var capturePlan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users",
            IsComplete = true,
            Entries = new[]
            {
                new V2CapturePlanEntry
                {
                    Name = "accepted",
                    TypeName = "int",
                    CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer,
                    InitializerExpression = "1",
                },
                new V2CapturePlanEntry
                {
                    Name = "rejected",
                    TypeName = "object",
                    CapturePolicy = LocalSymbolReplayPolicies.Reject,
                },
            },
        };

        var statements = RunnerGenerator.BuildV2CapturePlanInitialization(capturePlan);

        // Should generate only 1 statement (rejected entry skipped)
        Assert.Single(statements);
    }

    [Fact]
    public void IsV2CapturePlanEligible_NullPlan_ReturnsFalse()
    {
        var eligible = RunnerGenerator.IsV2CapturePlanEligible(null);

        Assert.False(eligible);
    }

    [Fact]
    public void IsV2CapturePlanEligible_Incompleteplan_ReturnsFalse()
    {
        var capturePlan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users",
            IsComplete = false, // Incomplete
        };

        var eligible = RunnerGenerator.IsV2CapturePlanEligible(capturePlan);

        Assert.False(eligible);
    }

    [Fact]
    public void IsV2CapturePlanEligible_WithDiagnostics_ReturnsFalse()
    {
        var capturePlan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users",
            IsComplete = true,
            Diagnostics = new[]
            {
                new V2CaptureDiagnostic
                {
                    Code = "ERROR",
                    SymbolName = "x",
                    Message = "Capture failed",
                },
            },
        };

        var eligible = RunnerGenerator.IsV2CapturePlanEligible(capturePlan);

        Assert.False(eligible);
    }

    [Fact]
    public void IsV2CapturePlanEligible_CompleteAndValid_ReturnsTrue()
    {
        var capturePlan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users",
            IsComplete = true,
            Entries = new[] { new V2CapturePlanEntry { Name = "u", TypeName = "User", CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer } },
        };

        var eligible = RunnerGenerator.IsV2CapturePlanEligible(capturePlan);

        Assert.True(eligible);
    }

    [Fact]
    public void CountV2ExecutableEntries_AllReplayPolicies_CountsAll()
    {
        var capturePlan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users",
            IsComplete = true,
            Entries = new[]
            {
                new V2CapturePlanEntry { Name = "a", TypeName = "int", CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer },
                new V2CapturePlanEntry { Name = "b", TypeName = "string", CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer },
            },
        };

        var count = RunnerGenerator.CountV2ExecutableEntries(capturePlan);

        Assert.Equal(2, count);
    }

    [Fact]
    public void CountV2ExecutableEntries_MixedPolicies_ExcludesReject()
    {
        var capturePlan = new V2CapturePlanSnapshot
        {
            ExecutableExpression = "db.Users",
            IsComplete = true,
            Entries = new[]
            {
                new V2CapturePlanEntry { Name = "a", TypeName = "int", CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer },
                new V2CapturePlanEntry { Name = "b", TypeName = "string", CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder },
                new V2CapturePlanEntry { Name = "c", TypeName = "object", CapturePolicy = LocalSymbolReplayPolicies.Reject },
            },
        };

        var count = RunnerGenerator.CountV2ExecutableEntries(capturePlan);

        Assert.Equal(2, count); // Only non-Reject entries
    }

    [Fact]
    public void CountV2ExecutableEntries_NullPlan_ReturnsZero()
    {
        var count = RunnerGenerator.CountV2ExecutableEntries(null);

        Assert.Equal(0, count);
    }
}
