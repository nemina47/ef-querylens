using EFQueryLens.Lsp;

namespace EFQueryLens.Integration.Tests.Lsp;

/// <summary>
/// Tests for <see cref="LspEnvironment"/> — centralized environment variable parsing
/// for LSP configuration, client detection, and action server port resolution.
/// </summary>
public class LspEnvironmentTests : IDisposable
{
    private readonly Dictionary<string, string?> _savedEnv = new();

    public LspEnvironmentTests()
    {
        // Backup all QUERYLENS_* vars for cleanup
        foreach (var key in Environment.GetEnvironmentVariables().Keys.OfType<string>()
                     .Where(k => k.StartsWith("QUERYLENS_", StringComparison.OrdinalIgnoreCase)))
        {
            _savedEnv[key] = Environment.GetEnvironmentVariable(key);
        }
    }

    public void Dispose()
    {
        // Restore original environment
        foreach (var saved in _savedEnv)
        {
            if (saved.Value == null)
            {
                Environment.SetEnvironmentVariable(saved.Key, null);
            }
            else
            {
                Environment.SetEnvironmentVariable(saved.Key, saved.Value);
            }
        }
    }

    private static void SetEnv(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
    }

    // ── ReadBool: default fallback ───────────────────────────────────────────

    [Fact]
    public void ReadBool_UnsetVariable_ReturnsFallback()
    {
        SetEnv("QUERYLENS_TEST_BOOL", null);

        var result = LspEnvironment.ReadBool("QUERYLENS_TEST_BOOL", fallback: true);

        Assert.True(result);
    }

    [Fact]
    public void ReadBool_EmptyString_ReturnsFallback()
    {
        SetEnv("QUERYLENS_TEST_BOOL", "");

        var result = LspEnvironment.ReadBool("QUERYLENS_TEST_BOOL", fallback: false);

        Assert.False(result);
    }

    // ── ReadBool: standard boolean parsing ───────────────────────────────────

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void ReadBool_StandardBooleanStrings_ParsesCorrectly(string value)
    {
        SetEnv("QUERYLENS_TEST_BOOL", value);

        var result = LspEnvironment.ReadBool("QUERYLENS_TEST_BOOL", fallback: false);

        Assert.True(result);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void ReadBool_FalseStrings_ParsesCorrectly(string value)
    {
        SetEnv("QUERYLENS_TEST_BOOL", value);

        var result = LspEnvironment.ReadBool("QUERYLENS_TEST_BOOL", fallback: true);

        Assert.False(result);
    }

    // ── ReadBool: legacy boolean formats (1/yes/on) ──────────────────────────

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("Yes")]
    [InlineData("YES")]
    [InlineData("on")]
    [InlineData("ON")]
    public void ReadBool_LegacyTruthyValues_ParsesAsTrue(string value)
    {
        SetEnv("QUERYLENS_TEST_BOOL", value);

        var result = LspEnvironment.ReadBool("QUERYLENS_TEST_BOOL", fallback: false);

        Assert.True(result);
    }

    // ── ReadInt: bounded range validation ────────────────────────────────────

    [Fact]
    public void ReadInt_ValueWithinRange_ReturnsParsedValue()
    {
        SetEnv("QUERYLENS_TEST_INT", "5000");

        var result = LspEnvironment.ReadInt("QUERYLENS_TEST_INT", fallback: 0, min: 1000, max: 9999);

        Assert.Equal(5000, result);
    }

    [Fact]
    public void ReadInt_ValueBelowMin_ReturnsMin()
    {
        SetEnv("QUERYLENS_TEST_INT", "500");

        var result = LspEnvironment.ReadInt("QUERYLENS_TEST_INT", fallback: 0, min: 1000, max: 9999);

        Assert.Equal(1000, result);
    }

    [Fact]
    public void ReadInt_ValueAboveMax_ReturnsMax()
    {
        SetEnv("QUERYLENS_TEST_INT", "10000");

        var result = LspEnvironment.ReadInt("QUERYLENS_TEST_INT", fallback: 0, min: 1000, max: 9999);

        Assert.Equal(9999, result);
    }

    [Fact]
    public void ReadInt_NonNumeric_ReturnsFallback()
    {
        SetEnv("QUERYLENS_TEST_INT", "not_a_number");

        var result = LspEnvironment.ReadInt("QUERYLENS_TEST_INT", fallback: 2000, min: 1000, max: 9999);

        Assert.Equal(2000, result);
    }

    [Fact]
    public void ReadInt_Unset_ReturnsFallback()
    {
        SetEnv("QUERYLENS_TEST_INT", null);

        var result = LspEnvironment.ReadInt("QUERYLENS_TEST_INT", fallback: 3000, min: 1000, max: 9999);

        Assert.Equal(3000, result);
    }

    // ── TryReadOptionalInt: returns null on parse failure ─────────────────────

    [Fact]
    public void TryReadOptionalInt_ValidWithinRange_ReturnsValue()
    {
        SetEnv("QUERYLENS_OPTIONAL_INT", "7500");

        var result = LspEnvironment.TryReadOptionalInt("QUERYLENS_OPTIONAL_INT", min: 1000, max: 9999);

        Assert.NotNull(result);
        Assert.Equal(7500, result);
    }

    [Fact]
    public void TryReadOptionalInt_UnsetVariable_ReturnsNull()
    {
        SetEnv("QUERYLENS_OPTIONAL_INT", null);

        var result = LspEnvironment.TryReadOptionalInt("QUERYLENS_OPTIONAL_INT", min: 1000, max: 9999);

        Assert.Null(result);
    }

    [Fact]
    public void TryReadOptionalInt_NonNumeric_ReturnsNull()
    {
        SetEnv("QUERYLENS_OPTIONAL_INT", "invalid");

        var result = LspEnvironment.TryReadOptionalInt("QUERYLENS_OPTIONAL_INT", min: 1000, max: 9999);

        Assert.Null(result);
    }

    [Fact]
    public void TryReadOptionalInt_OutOfRange_ReturnsNull()
    {
        SetEnv("QUERYLENS_OPTIONAL_INT", "500");

        var result = LspEnvironment.TryReadOptionalInt("QUERYLENS_OPTIONAL_INT", min: 1000, max: 9999);

        Assert.Null(result);
    }
}

