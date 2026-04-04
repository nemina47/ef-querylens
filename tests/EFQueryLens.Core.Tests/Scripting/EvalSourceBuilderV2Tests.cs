using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;
using Xunit;

namespace EFQueryLens.Core.Tests.Scripting;

/// <summary>
/// Unit tests for EvalSourceBuilder V2 capture-plan support (Slice 3b step 2).
/// Tests policy-driven code generation for symbol initialization.
/// </summary>
public class EvalSourceBuilderV2Tests
{
    [Fact]
    public void BuildV2CaptureInitializationCode_ReplayInitializer_EmitsReplayCode()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "user",
            TypeName = "User",
            CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            InitializerExpression = "new User { Id = 1 }",
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("var user =", code);
        Assert.Contains("new User { Id = 1 }", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_ReplayWithoutExpression_EmitsDefault()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "user",
            TypeName = "User",
            CapturePolicy = LocalSymbolReplayPolicies.ReplayInitializer,
            // InitializerExpression is null
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("var user =", code);
        Assert.Contains("default(User)", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_EmitsDefaultValue()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "pageSize",
            TypeName = "int",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("var pageSize =", code);
        Assert.Contains("default(int)", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_Reject_ReturnsNull()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "rejected",
            TypeName = "object",
            CapturePolicy = LocalSymbolReplayPolicies.Reject,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.Null(code);
    }

    [Theory]
    [InlineData(LocalSymbolReplayPolicies.ReplayInitializer, true)]
    [InlineData(LocalSymbolReplayPolicies.UsePlaceholder, true)]
    [InlineData(LocalSymbolReplayPolicies.Reject, false)]
    public void BuildV2CaptureInitializationCode_AllPolicies_ProducesCorrectOutput(
        string policy, bool shouldProduceCode)
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "x",
            TypeName = "int",
            CapturePolicy = policy,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        if (shouldProduceCode)
        {
            Assert.NotNull(code);
            Assert.Contains("var x =", code);
        }
        else
        {
            Assert.Null(code);
        }
    }
}
