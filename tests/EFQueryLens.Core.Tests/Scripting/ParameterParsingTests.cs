using System.Reflection;
using EFQueryLens.Core.Contracts;
using QueryEvaluator = EFQueryLens.Core.Scripting.Evaluation.QueryEvaluator;

namespace EFQueryLens.Core.Tests.Scripting;

public class ParameterParsingTests
{
    private static readonly MethodInfo s_parseParameters =
        typeof(QueryEvaluator).GetMethod("ParseParameters", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find QueryEvaluator.ParseParameters via reflection.");

    private static readonly MethodInfo s_extractDbType =
        typeof(QueryEvaluator).GetMethod("ExtractDbType", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find QueryEvaluator.ExtractDbType via reflection.");

    private static readonly MethodInfo s_extractInferredValue =
        typeof(QueryEvaluator).GetMethod("ExtractInferredValue", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find QueryEvaluator.ExtractInferredValue via reflection.");

    private static IReadOnlyList<QueryParameter> ParseParameters(string sql) =>
        (IReadOnlyList<QueryParameter>)s_parseParameters.Invoke(null, [sql])!;

    private static string ExtractDbType(string annotation) =>
        (string)s_extractDbType.Invoke(null, [annotation])!;

    private static string? ExtractInferredValue(string annotation) =>
        (string?)s_extractInferredValue.Invoke(null, [annotation]);

    // ─── ParseParameters ──────────────────────────────────────────────────────

    [Fact]
    public void ParseParameters_EmptySql_ReturnsEmpty()
    {
        var result = ParseParameters(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseParameters_SqlWithNoComments_ReturnsEmpty()
    {
        var result = ParseParameters("SELECT * FROM Orders WHERE Id = @p0");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseParameters_SingleAnnotatedParameter_ReturnsParsedParameter()
    {
        var sql = "SELECT * FROM Orders\n-- @p0='5' (DbType = Int32)";

        var result = ParseParameters(sql);

        Assert.Single(result);
        Assert.Equal("@p0", result[0].Name);
        Assert.Equal("Int32", result[0].ClrType);
        Assert.Equal("5", result[0].InferredValue);
    }

    [Fact]
    public void ParseParameters_MultipleAnnotatedParameters_ReturnsAll()
    {
        var sql = """
            SELECT * FROM Orders
            -- @p0='123' (DbType = Int32)
            -- @p1='hello' (DbType = String)
            """;

        var result = ParseParameters(sql);

        Assert.Equal(2, result.Count);
        Assert.Equal("@p0", result[0].Name);
        Assert.Equal("@p1", result[1].Name);
    }

    [Fact]
    public void ParseParameters_LineWithoutAtPrefix_IsSkipped()
    {
        // A regular SQL comment (no @) should not produce a parameter.
        var sql = "SELECT 1\n-- some diagnostic comment\n-- @p0='1' (DbType = Int32)";

        var result = ParseParameters(sql);

        Assert.Single(result);
    }

    [Fact]
    public void ParseParameters_LineWithMalformedAnnotation_IsSkipped()
    {
        // No '=' or space after name → nameEnd < 0 → skipped.
        var sql = "SELECT 1\n-- @badparam";

        var result = ParseParameters(sql);

        Assert.Empty(result);
    }

    // ─── ExtractDbType ────────────────────────────────────────────────────────

    [Fact]
    public void ExtractDbType_WithDbTypeAnnotation_ReturnsTypeName()
    {
        var result = ExtractDbType("@p0='5' (DbType = Int32)");
        Assert.Equal("Int32", result);
    }

    [Fact]
    public void ExtractDbType_WithStringDbType_ReturnsString()
    {
        var result = ExtractDbType("@p1='hello' (DbType = String)");
        Assert.Equal("String", result);
    }

    [Fact]
    public void ExtractDbType_WithNoDbTypeMarker_ReturnsObject()
    {
        var result = ExtractDbType("@p0='5'");
        Assert.Equal("object", result);
    }

    [Fact]
    public void ExtractDbType_WithMalformedClosingParen_ReturnsObject()
    {
        // DbType marker found but no closing ')' after it → returns "object"
        var result = ExtractDbType("@p0='5' (DbType = Int32");
        Assert.Equal("object", result);
    }

    // ─── ExtractInferredValue ─────────────────────────────────────────────────

    [Fact]
    public void ExtractInferredValue_WithQuotedValue_ReturnsValue()
    {
        var result = ExtractInferredValue("@p0='hello' (DbType = String)");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ExtractInferredValue_WithNumericValue_ReturnsAsString()
    {
        var result = ExtractInferredValue("@p0='123' (DbType = Int32)");
        Assert.Equal("123", result);
    }

    [Fact]
    public void ExtractInferredValue_WithNoQuotes_ReturnsNull()
    {
        var result = ExtractInferredValue("@p0 (DbType = Int32)");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractInferredValue_WithEmptyQuotes_ReturnsNull()
    {
        // "='" found but no closing "'" → e > s is false → returns null
        var result = ExtractInferredValue("@p0='");
        Assert.Null(result);
    }
}
