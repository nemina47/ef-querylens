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
    private sealed class NestedTypeContainer
    {
        public enum NestedEnum
        {
            First,
            Second,
        }
    }

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
    public void BuildV2CaptureInitializationCode_UsePlaceholder_Int_EmitsCanonicalValue()
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
        Assert.Contains("1", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_String_EmitsSentinelValue()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "term",
            TypeName = "string",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("var term =", code);
        Assert.Contains("\"qlstub0\"", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_Guid_EmitsDeterministicGuid()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "id",
            TypeName = "Guid",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("Guid.Parse", code);
        Assert.Contains("11111111-1111-1111-1111-111111111111", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_DateTime_EmitsUtcNow()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "createdBefore",
            TypeName = "DateTime",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("global::System.DateTime.UtcNow", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_DateOnly_EmitsTodayFromUtcNow()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "createdOn",
            TypeName = "DateOnly",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("global::System.DateOnly.FromDateTime(global::System.DateTime.UtcNow)", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_TimeOnly_EmitsNowFromUtcNow()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "createdAt",
            TypeName = "TimeOnly",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("global::System.TimeOnly.FromDateTime(global::System.DateTime.UtcNow)", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_Array_SeedsTwoDeterministicItems()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "ids",
            TypeName = "int[]",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("new int[]", code);
        Assert.Contains("{ 1, 2 }", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_List_SeedsTwoDeterministicItems()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "terms",
            TypeName = "List<string>",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("new global::System.Collections.Generic.List<string>", code);
        Assert.Contains("\"qlstub0\"", code);
        Assert.Contains("\"qlstub1\"", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_ListOfNestedEnum_UsesCompilableTypeName()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "clearSections",
            TypeName = $"global::System.Collections.Generic.List<{typeof(NestedTypeContainer.NestedEnum).FullName}>",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains(
            "new global::System.Collections.Generic.List<EFQueryLens.Core.Tests.Scripting.EvalSourceBuilderV2Tests.NestedTypeContainer.NestedEnum>",
            code);
        Assert.DoesNotContain("+", code);
        Assert.Contains(
            "typeof(EFQueryLens.Core.Tests.Scripting.EvalSourceBuilderV2Tests.NestedTypeContainer.NestedEnum).IsEnum",
            code);
        Assert.Contains(
            "Enum.GetValues(typeof(EFQueryLens.Core.Tests.Scripting.EvalSourceBuilderV2Tests.NestedTypeContainer.NestedEnum))",
            code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_IEnumerable_SeedsTwoDeterministicItems()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "ids",
            TypeName = "IEnumerable<int>",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("new int[]", code);
        Assert.Contains("{ 1, 2 }", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_IReadOnlyCollection_SeedsTwoDeterministicItems()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "ids",
            TypeName = "IReadOnlyCollection<System.Guid>",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("new global::System.Collections.Generic.List<System.Guid>", code);
        Assert.Contains("11111111-1111-1111-1111-111111111111", code);
        Assert.Contains("22222222-2222-2222-2222-222222222222", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_IReadOnlyListNestedEnum_UsesCompilableTypeName()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "riskClasses",
            TypeName = $"global::System.Collections.Generic.IReadOnlyList<{typeof(NestedTypeContainer.NestedEnum).FullName}>",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains(
            "new global::System.Collections.Generic.List<EFQueryLens.Core.Tests.Scripting.EvalSourceBuilderV2Tests.NestedTypeContainer.NestedEnum>",
            code);
        Assert.DoesNotContain("+", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_HashSet_SeedsTwoDeterministicItems()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "ids",
            TypeName = "HashSet<int>",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("new global::System.Collections.Generic.HashSet<int>", code);
        Assert.Contains("{ 1, 2 }", code);
    }

    // ── Operator-aware synthesis ──────────────────────────────────────────────────

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_CancellationToken_EmitsNone()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "ct",
            TypeName = "System.Threading.CancellationToken",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("CancellationToken.None", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_TimeSpan_EmitsZero()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "timeout",
            TypeName = "System.TimeSpan",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("TimeSpan.Zero", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_Char_EmitsLiteral()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "separator",
            TypeName = "char",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("'a'", code);
    }

    [Theory]
    [InlineData("sbyte", "(sbyte)1")]
    [InlineData("ushort", "(ushort)1")]
    [InlineData("uint", "1u")]
    [InlineData("ulong", "1ul")]
    [InlineData("nint", "(nint)1")]
    [InlineData("nuint", "(nuint)1")]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_IntegralVariants_EmitCanonicalValues(
        string typeName, string expected)
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "value",
            TypeName = typeName,
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains(expected, code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_SelectorHint_WithObjectReturn_EmitsIdentityLambda()
    {
        var typeName = "global::System.Linq.Expressions.Expression<global::System.Func<global::MyApp.Order, object>>";
        var entry = new V2CapturePlanEntry
        {
            Name = "expression",
            TypeName = typeName,
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            QueryUsageHint = QueryUsageHints.SelectorExpression,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("var expression =", code);
        Assert.Contains("(object)e", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_UsePlaceholder_ExpressionPredicateWithoutHint_EmitsNonNullLambda()
    {
        var typeName = "global::System.Linq.Expressions.Expression<global::System.Func<global::MyApp.Order, bool>>";
        var entry = new V2CapturePlanEntry
        {
            Name = "filter",
            TypeName = typeName,
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("var filter =", code);
        Assert.Contains("(e => true)", code);
        Assert.DoesNotContain("default(global::System.Linq.Expressions.Expression", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_StringPrefixHint_EmitsShorterValue()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "prefix",
            TypeName = "string",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            QueryUsageHint = QueryUsageHints.StringPrefix,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("\"ql\"", code);
    }

    [Fact]
    public void BuildV2CaptureInitializationCode_StringSuffixHint_EmitsShorterValue()
    {
        var entry = new V2CapturePlanEntry
        {
            Name = "suffix",
            TypeName = "string",
            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            QueryUsageHint = QueryUsageHints.StringSuffix,
        };

        var code = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);

        Assert.NotNull(code);
        Assert.Contains("\"stub\"", code);
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
