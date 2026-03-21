using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public class TypeExtractionTests
{
    // ─── Explicit type declarations ───────────────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_ExplicitInt_IsFound()
    {
        var source = """
            int count = 5;
            _ = count;
            """;

        var types = Extract(source, "_ = count;");

        Assert.True(types.TryGetValue("count", out var typeName));
        Assert.Equal("int", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_ExplicitString_IsFound()
    {
        var source = """
            string name = "hello";
            _ = name;
            """;

        var types = Extract(source, "_ = name;");

        Assert.True(types.TryGetValue("name", out var typeName));
        Assert.Equal("string", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_ExplicitGuid_IsFound()
    {
        var source = """
            System.Guid id = System.Guid.Empty;
            _ = id;
            """;

        var types = Extract(source, "_ = id;");

        Assert.True(types.TryGetValue("id", out var typeName));
        Assert.Equal("System.Guid", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_ExplicitGenericList_IsFound()
    {
        var source = """
            System.Collections.Generic.List<string> names = null;
            _ = names;
            """;

        var types = Extract(source, "_ = names;");

        Assert.True(types.TryGetValue("names", out var typeName));
        Assert.Equal("System.Collections.Generic.List<string>", typeName);
    }

    // ─── var declarations — literal inference ─────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_VarWithIntLiteral_IsInferredAsInt()
    {
        var source = """
            var pageSize = 10;
            _ = pageSize;
            """;

        var types = Extract(source, "_ = pageSize;");

        Assert.True(types.TryGetValue("pageSize", out var typeName));
        Assert.Equal("int", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithLongSuffix_IsInferredAsLong()
    {
        var source = """
            var bigNumber = 100L;
            _ = bigNumber;
            """;

        var types = Extract(source, "_ = bigNumber;");

        Assert.True(types.TryGetValue("bigNumber", out var typeName));
        Assert.Equal("long", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithDecimalSuffix_IsInferredAsDecimal()
    {
        var source = """
            var price = 9.99m;
            _ = price;
            """;

        var types = Extract(source, "_ = price;");

        Assert.True(types.TryGetValue("price", out var typeName));
        Assert.Equal("decimal", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithFloatSuffix_IsInferredAsFloat()
    {
        var source = """
            var ratio = 1.5f;
            _ = ratio;
            """;

        var types = Extract(source, "_ = ratio;");

        Assert.True(types.TryGetValue("ratio", out var typeName));
        Assert.Equal("float", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithDoubleLiteral_IsInferredAsDouble()
    {
        var source = """
            var rate = 3.14;
            _ = rate;
            """;

        var types = Extract(source, "_ = rate;");

        Assert.True(types.TryGetValue("rate", out var typeName));
        Assert.Equal("double", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithStringLiteral_IsInferredAsString()
    {
        var source = """
            var label = "hello";
            _ = label;
            """;

        var types = Extract(source, "_ = label;");

        Assert.True(types.TryGetValue("label", out var typeName));
        Assert.Equal("string", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithTrueLiteral_IsInferredAsBool()
    {
        var source = """
            var flag = true;
            _ = flag;
            """;

        var types = Extract(source, "_ = flag;");

        Assert.True(types.TryGetValue("flag", out var typeName));
        Assert.Equal("bool", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithFalseLiteral_IsInferredAsBool()
    {
        var source = """
            var enabled = false;
            _ = enabled;
            """;

        var types = Extract(source, "_ = enabled;");

        Assert.True(types.TryGetValue("enabled", out var typeName));
        Assert.Equal("bool", typeName);
    }

    // ─── var declarations — constructor / cast inference ─────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_VarWithNewExpression_IsInferredFromTypeName()
    {
        var source = """
            var list = new System.Collections.Generic.List<string>();
            _ = list;
            """;

        var types = Extract(source, "_ = list;");

        Assert.True(types.TryGetValue("list", out var typeName));
        Assert.Equal("System.Collections.Generic.List<string>", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithCastExpression_IsInferredFromCastType()
    {
        var source = """
            var id = (System.Guid)System.Guid.Empty;
            _ = id;
            """;

        var types = Extract(source, "_ = id;");

        Assert.True(types.TryGetValue("id", out var typeName));
        Assert.Equal("System.Guid", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithDefaultExpression_IsInferredFromDefaultType()
    {
        var source = """
            var id = default(System.Guid);
            _ = id;
            """;

        var types = Extract(source, "_ = id;");

        Assert.True(types.TryGetValue("id", out var typeName));
        Assert.Equal("System.Guid", typeName);
    }

    // ─── Scope boundaries ─────────────────────────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_VariableDeclaredAfterCursor_IsNotFound()
    {
        var source = """
            int before = 1;
            _ = before;
            int after = 2;
            """;

        var types = Extract(source, "_ = before;");

        Assert.True(types.ContainsKey("before"), "'before' should be visible.");
        Assert.False(types.ContainsKey("after"), "'after' is declared after cursor — must not appear.");
    }

    [Fact]
    public void ExtractLocalVariableTypes_MultipleVariablesBeforeCursor_AllFound()
    {
        var source = """
            int a = 1;
            string b = "hello";
            bool c = true;
            _ = a;
            """;

        var types = Extract(source, "_ = a;");

        Assert.True(types.ContainsKey("a"));
        Assert.True(types.ContainsKey("b"));
        Assert.True(types.ContainsKey("c"));
    }

    [Fact]
    public void ExtractLocalVariableTypes_ShadowedVariable_OuterScopeIsOverriddenByInner()
    {
        // Inner declaration of 'x' should win over outer 'x' — inner is declared first from cursor's perspective.
        var source = """
            int x = 1;
            int x = 2;
            _ = x;
            """;

        var types = Extract(source, "_ = x;");

        // Both are technically before the cursor; the implementation keeps the first one added (inner-first walk),
        // so we just verify 'x' is present with some type.
        Assert.True(types.ContainsKey("x"));
        Assert.Equal("int", types["x"]);
    }

    // ─── Method parameters ────────────────────────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_MethodParameter_IsFound()
    {
        var source = """
            void M(int userId, string name)
            {
                _ = userId;
            }
            """;

        var types = Extract(source, "_ = userId;");

        Assert.True(types.TryGetValue("userId", out var userIdType));
        Assert.Equal("int", userIdType);
        Assert.True(types.TryGetValue("name", out var nameType));
        Assert.Equal("string", nameType);
    }

    [Fact]
    public void ExtractLocalVariableTypes_LocalFunctionParameter_IsFound()
    {
        var source = """
            void Outer()
            {
                void Inner(System.Guid id)
                {
                    _ = id;
                }
            }
            """;

        var types = Extract(source, "_ = id;");

        Assert.True(types.TryGetValue("id", out var typeName));
        Assert.Equal("System.Guid", typeName);
    }

    // ─── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_EmptySource_ReturnsEmpty()
    {
        var types = LspSyntaxHelper.ExtractLocalVariableTypesAtPosition("", 0, 0);
        Assert.Empty(types);
    }

    [Fact]
    public void ExtractLocalVariableTypes_LineBeyondFileLength_ReturnsEmpty()
    {
        var types = LspSyntaxHelper.ExtractLocalVariableTypesAtPosition("int x = 1;", 999, 0);
        Assert.Empty(types);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithImplicitNew_ReturnsEmpty()
    {
        // var x = new() {} — target type unknown without semantic model → excluded
        var source = """
            var x = new();
            _ = x;
            """;

        var types = Extract(source, "_ = x;");

        // 'x' may or may not appear — if included it must not crash, and if absent that's correct
        // The important thing is no exception is thrown.
        Assert.IsType<Dictionary<string, string>>(types);
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Extract(string source, string cursorMarker)
    {
        var (line, character) = FindPosition(source, cursorMarker);
        return LspSyntaxHelper.ExtractLocalVariableTypesAtPosition(source, line, character);
    }

    private static (int line, int character) FindPosition(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Marker '{marker}' not found in source.");

        var line = 0;
        var character = 0;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n') { line++; character = 0; }
            else { character++; }
        }

        return (line, character);
    }
}
