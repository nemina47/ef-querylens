using EFQueryLens.Core.Contracts;
using Xunit;

namespace EFQueryLens.Core.Tests.Scripting;

/// <summary>
/// Tests for V2 runtime decision-making (slice 3 analysis).
/// Verifies that the runtime correctly analyzes v2 payloads and makes deterministic execution decisions.
/// </summary>
public class V2RuntimeAnalyzerTests
{
    [Fact]
    public void Analyze_NoV2Payloads_ReturnsLegacyPath()
    {
        var request = new TranslationRequest
        {
            Expression = "db.Users.ToListAsync()",
            AssemblyPath = "/test.dll",
        };

        var decision = V2RuntimeAnalyzer.Analyze(request);

        Assert.False(decision.ShouldUseV2Path);
        Assert.Null(decision.BlockReason);
    }

    [Fact]
    public void Analyze_ExtractionWithoutCapture_ReturnsBlockedWithReason()
    {
        var request = new TranslationRequest
        {
            Expression = "db.Users.ToListAsync()",
            AssemblyPath = "/test.dll",
            V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
            {
                Expression = "db.Users",
                ContextVariableName = "db",
                RootContextVariableName = "db",
                RootMemberName = "Users",
                BoundaryKind = "Queryable",
                NeedsMaterialization = false,
            },
        };

        var decision = V2RuntimeAnalyzer.Analyze(request);

        Assert.False(decision.ShouldUseV2Path);
        Assert.Equal("incomplete-v2-state", decision.BlockReason);
        Assert.NotNull(decision.BlockMessage);
    }

    [Fact]
    public void Analyze_CapturePlanWithDiagnostics_ReturnsRejectedWithDiagnostic()
    {
        var request = new TranslationRequest
        {
            Expression = "db.Users.ToListAsync()",
            AssemblyPath = "/test.dll",
            V2CapturePlan = new V2CapturePlanSnapshot
            {
                ExecutableExpression = "db.Users",
                IsComplete = false,
                Diagnostics = new[]
                {
                    new V2CaptureDiagnostic
                    {
                        Code = "MISSING_SYMBOL",
                        SymbolName = "userId",
                        Message = "Symbol 'userId' cannot be captured.",
                    },
                },
            },
        };

        var decision = V2RuntimeAnalyzer.Analyze(request);

        Assert.False(decision.ShouldUseV2Path);
        Assert.StartsWith("capture-rejected:", decision.BlockReason);
        Assert.NotNull(decision.CapturePlan);
    }

    [Fact]
    public void Analyze_CompleteCaptureWithExtraction_ReturnsV2Path()
    {
        var request = new TranslationRequest
        {
            Expression = "db.Users.Where(u => u.IsActive).ToListAsync()",
            AssemblyPath = "/test.dll",
            V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
            {
                Expression = "db.Users.Where(u => u.IsActive)",
                ContextVariableName = "db",
                RootContextVariableName = "db",
                RootMemberName = "Users",
                BoundaryKind = "Queryable",
                NeedsMaterialization = true,
            },
            V2CapturePlan = new V2CapturePlanSnapshot
            {
                ExecutableExpression = "db.Users.Where(u => u.IsActive)",
                IsComplete = true,
                Entries = new[]
                {
                    new V2CapturePlanEntry
                    {
                        Name = "u",
                        TypeName = "User",
                        CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer,
                        InitializerExpression = "default(User)",
                    },
                },
            },
        };

        var decision = V2RuntimeAnalyzer.Analyze(request);

        Assert.True(decision.ShouldUseV2Path);
        Assert.Null(decision.BlockReason);
        Assert.NotNull(decision.ExtractionPlan);
        Assert.NotNull(decision.CapturePlan);
    }

    [Fact]
    public void Analyze_IncompleteCaptureWithoutDiagnostics_ReturnsBlocked()
    {
        var request = new TranslationRequest
        {
            Expression = "db.Users.ToListAsync()",
            AssemblyPath = "/test.dll",
            V2CapturePlan = new V2CapturePlanSnapshot
            {
                ExecutableExpression = "db.Users",
                IsComplete = false,
            },
        };

        var decision = V2RuntimeAnalyzer.Analyze(request);

        Assert.False(decision.ShouldUseV2Path);
        Assert.Equal("incomplete-capture-plan", decision.BlockReason);
    }

    [Theory]
    [InlineData(LocalSymbolReplayPolicies.ReplayInitializer, true, false, false)]
    [InlineData(LocalSymbolReplayPolicies.UsePlaceholder, false, true, false)]
    [InlineData(LocalSymbolReplayPolicies.Reject, false, false, true)]
    public void CapturePolicy_Classification_ReturnsCorrectFlags(string policy, bool expectReplay, bool expectPlaceholder, bool expectReject)
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "x",
            TypeName = "int",
            CapturePolicy = policy,
        };

        Assert.Equal(expectReplay, V2RuntimeAnalyzer.IsReplayInitializer(entry));
        Assert.Equal(expectPlaceholder, V2RuntimeAnalyzer.IsPlaceholder(entry));
        Assert.Equal(expectReject, V2RuntimeAnalyzer.IsRejected(entry));
    }

    [Fact]
    public void FormatDiagnostic_ValidBlockReason_ReturnsFormattedMessage()
    {
        var decision = new V2RuntimeDecision
        {
            ShouldUseV2Path = false,
            BlockReason = "test-reason",
            BlockMessage = "Test message for diagnostics",
        };

        var formatted = V2RuntimeAnalyzer.FormatDiagnostic(decision);

        Assert.Contains("test-reason", formatted);
        Assert.Contains("Test message", formatted);
    }

    [Fact]
    public void FormatDiagnostic_NoBlockReason_ReturnsDefaultMessage()
    {
        var decision = new V2RuntimeDecision { ShouldUseV2Path = false };

        var formatted = V2RuntimeAnalyzer.FormatDiagnostic(decision);

        Assert.Contains("no v2 diagnostic", formatted);
    }
}
