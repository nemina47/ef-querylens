using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using QueryEvaluator = EFQueryLens.Core.Scripting.Evaluation.QueryEvaluator;

namespace EFQueryLens.Core.Tests.Scripting;

public class DiagnosticTranslationTests
{
    private static readonly MethodInfo s_formatHardDiagnostics =
        typeof(QueryEvaluator).GetMethod("FormatHardDiagnostics", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find QueryEvaluator.FormatHardDiagnostics via reflection.");

    private static readonly MethodInfo s_formatSoftDiagnostics =
        typeof(QueryEvaluator).GetMethod("FormatSoftDiagnostics", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find QueryEvaluator.FormatSoftDiagnostics via reflection.");

    private static string FormatHard(IEnumerable<Diagnostic> errors) =>
        (string)s_formatHardDiagnostics.Invoke(null, [errors])!;

    private static string FormatSoft(IEnumerable<Diagnostic> errors) =>
        (string)s_formatSoftDiagnostics.Invoke(null, [errors])!;

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<Diagnostic> Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "DiagnosticTranslationTest",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
    }

    private static int CountSubstring(string source, string sub)
    {
        var count = 0;
        var i = 0;
        while ((i = source.IndexOf(sub, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += sub.Length;
        }
        return count;
    }

    // ─── FormatHardDiagnostics ────────────────────────────────────────────────

    [Fact]
    public void FormatHardDiagnostics_SingleError_IncludesCodeAndRawMessage()
    {
        var errors = Compile("class C { void M() { var x = unknownVar; } }");
        var cs0103 = errors.Where(e => e.Id == "CS0103").ToList();
        Assert.NotEmpty(cs0103);

        var result = FormatHard(cs0103);

        Assert.Contains("CS0103", result, StringComparison.Ordinal);
        Assert.Contains("unknownVar", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHardDiagnostics_MultipleErrors_JoinedWithSemicolon()
    {
        var errors = Compile("class C { void M() { var x = foo; var y = bar; } }");
        var twoErrors = errors.Where(e => e.Id == "CS0103").Take(2).ToList();
        Assert.Equal(2, twoErrors.Count);

        var result = FormatHard(twoErrors);

        Assert.Contains("; ", result, StringComparison.Ordinal);
        // Both identifiers must appear
        Assert.Contains("foo", result, StringComparison.Ordinal);
        Assert.Contains("bar", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHardDiagnostics_EmptyList_ReturnsEmptyString()
    {
        var result = FormatHard([]);
        Assert.Equal(string.Empty, result);
    }

    // ─── FormatSoftDiagnostics — CS0103 ──────────────────────────────────────

    [Fact]
    public void FormatSoftDiagnostics_CS0103_TranslatesToUnknownVariableHint()
    {
        var errors = Compile("class C { void M() { var x = unknownVar; } }");
        var cs0103 = errors.Where(e => e.Id == "CS0103").ToList();
        Assert.NotEmpty(cs0103);

        var result = FormatSoft(cs0103);

        Assert.Contains("Unknown variable 'unknownVar'", result, StringComparison.Ordinal);
        Assert.Contains("var unknownVar = default;", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSoftDiagnostics_CS0103_HintIncludesTheIdentifierName()
    {
        var errors = Compile("class C { void M() { var x = mySpecificName; } }");
        var cs0103 = errors.Where(e => e.Id == "CS0103").ToList();
        Assert.NotEmpty(cs0103);

        var result = FormatSoft(cs0103);

        Assert.Contains("mySpecificName", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSoftDiagnostics_DuplicateCS0103SameIdentifier_Deduplicates()
    {
        // Two uses of the same unknown identifier → two CS0103 errors with same message.
        var errors = Compile("class C { void M() { var a = same; var b = same; } }");
        var cs0103 = errors.Where(e => e.Id == "CS0103" && e.GetMessage().Contains("same")).ToList();
        Assert.True(cs0103.Count >= 2, "Expected at least 2 CS0103 errors for 'same'.");

        var result = FormatSoft(cs0103);

        // Hint for 'same' should appear exactly once — duplicates are collapsed.
        Assert.Equal(1, CountSubstring(result, "Unknown variable 'same'"));
    }

    // ─── FormatSoftDiagnostics — CS0246 ──────────────────────────────────────

    [Fact]
    public void FormatSoftDiagnostics_CS0246_TranslatesToTypeNotFoundHint()
    {
        var errors = Compile("class C { MissingType _field; }");
        var cs0246 = errors.Where(e => e.Id == "CS0246").ToList();
        Assert.NotEmpty(cs0246);

        var result = FormatSoft(cs0246);

        Assert.Contains("Type 'MissingType' not found", result, StringComparison.Ordinal);
        Assert.Contains("using directive", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatSoftDiagnostics_CS0234_TranslatesToTypeNotFoundHint()
    {
        // CS0234: type or namespace 'X' does not exist in namespace 'Y'
        var errors = Compile("using My.Missing.Namespace;  class C { }");
        var cs0234 = errors.Where(e => e.Id is "CS0234" or "CS0246").ToList();

        if (cs0234.Count == 0)
            return; // Skip if compiler picks a different error for this source

        var result = FormatSoft(cs0234);

        // Both CS0234 and CS0246 should produce the same "Type not found" message family.
        Assert.True(
            result.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || result.Contains("using directive", StringComparison.OrdinalIgnoreCase),
            $"Expected a 'not found' hint but got: {result}");
    }

    // ─── FormatSoftDiagnostics — CS1061 ──────────────────────────────────────

    [Fact]
    public void FormatSoftDiagnostics_CS1061_TranslatesToExtensionMethodHint()
    {
        var errors = Compile("class C { void M() { int x = 5; var y = x.NonexistentMethod(); } }");
        var cs1061 = errors.Where(e => e.Id == "CS1061").ToList();
        Assert.NotEmpty(cs1061);

        var result = FormatSoft(cs1061);

        Assert.Contains("Extension method or member not in scope", result, StringComparison.Ordinal);
        Assert.Contains("using directives", result, StringComparison.Ordinal);
    }

    // ─── FormatSoftDiagnostics — CS7036 ──────────────────────────────────────

    [Fact]
    public void FormatSoftDiagnostics_CS7036_TranslatesToMissingArgumentHint()
    {
        var errors = Compile("class C { void Foo(int x) {} void M() { Foo(); } }");
        var cs7036 = errors.Where(e => e.Id == "CS7036").ToList();
        Assert.NotEmpty(cs7036);

        var result = FormatSoft(cs7036);

        Assert.Contains("Missing required argument", result, StringComparison.Ordinal);
    }

    // ─── FormatSoftDiagnostics — CS0019 ──────────────────────────────────────

    [Fact]
    public void FormatSoftDiagnostics_CS0019_TranslatesToOperatorHint()
    {
        // CS0019: Operator '>' cannot be applied to operands of type 'bool' and 'bool'
        var errors = Compile("class C { bool M() { return true > false; } }");
        var cs0019 = errors.Where(e => e.Id == "CS0019").ToList();
        Assert.NotEmpty(cs0019);

        var result = FormatSoft(cs0019);

        Assert.Contains("Operator cannot be applied to the operand types", result, StringComparison.Ordinal);
    }

    // ─── FormatSoftDiagnostics — unmapped error code ─────────────────────────

    [Fact]
    public void FormatSoftDiagnostics_UnmappedCode_PassesThroughAsCodeColonMessage()
    {
        // CS0131: "The left-hand side of an assignment must be a variable, property or indexer"
        var errors = Compile("class C { void M() { 5 = 10; } }");
        var nonMapped = errors
            .Where(e => e.Id is not ("CS0103" or "CS0246" or "CS0234" or "CS0400"
                or "CS1061" or "CS1929" or "CS7036" or "CS0019"))
            .ToList();

        if (nonMapped.Count == 0)
            return; // Skip if compiler emits only mapped codes for this source

        var result = FormatSoft(nonMapped);

        // Fallthrough format is "{Id}: {Message}"
        Assert.Matches(@"CS\d+:", result);
    }

    // ─── Mixed hard + soft comparison ────────────────────────────────────────

    [Fact]
    public void FormatHardDiagnostics_VS_FormatSoftDiagnostics_CS0103_ProduceDifferentOutput()
    {
        var errors = Compile("class C { void M() { var x = myVar; } }");
        var cs0103 = errors.Where(e => e.Id == "CS0103").ToList();
        Assert.NotEmpty(cs0103);

        var hard = FormatHard(cs0103);
        var soft = FormatSoft(cs0103);

        // Hard includes raw CS0103 prefix; soft translates to actionable hint.
        Assert.Contains("CS0103", hard, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0103", soft, StringComparison.Ordinal);
        Assert.Contains("Unknown variable", soft, StringComparison.Ordinal);
    }
}
