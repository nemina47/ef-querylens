using EFQueryLens.Lsp;
using Newtonsoft.Json.Linq;

namespace EFQueryLens.Integration.Tests.Lsp;

/// <summary>
/// Tests for <see cref="LspClientConfiguration"/> — pure JSON parsing, no live connection.
/// </summary>
public class LspClientConfigurationTests
{
    // ── FromInitializeRequest ────────────────────────────────────────────────

    [Fact]
    public void FromInitializeRequest_NullToken_ReturnsDefaultConfig()
    {
        var config = LspClientConfiguration.FromInitializeRequest(null);

        Assert.Null(config.DebugEnabled);
        Assert.Null(config.EnableLspHover);
        Assert.Null(config.HoverCacheTtlMs);
    }

    [Fact]
    public void FromInitializeRequest_WithInitializationOptions_ParsesSettings()
    {
        var token = JObject.Parse("""
            {
              "initializationOptions": {
                "queryLens": {
                  "debugEnabled": true,
                  "hoverCacheTtlMs": 5000
                }
              }
            }
            """);

        var config = LspClientConfiguration.FromInitializeRequest(token);

        Assert.True(config.DebugEnabled);
        Assert.Equal(5000, config.HoverCacheTtlMs);
    }

    [Fact]
    public void FromInitializeRequest_MissingInitializationOptions_ReturnsDefault()
    {
        var token = JObject.Parse("""{ "other": "value" }""");

        var config = LspClientConfiguration.FromInitializeRequest(token);

        Assert.Null(config.DebugEnabled);
    }

    // ── FromConfigurationChangeRequest ──────────────────────────────────────

    [Fact]
    public void FromConfigurationChangeRequest_NullToken_ReturnsDefault()
    {
        var config = LspClientConfiguration.FromConfigurationChangeRequest(null);

        Assert.Null(config.DebugEnabled);
    }

    [Fact]
    public void FromConfigurationChangeRequest_WithSettings_ParsesSettings()
    {
        var token = JObject.Parse("""
            {
              "settings": {
                "efQueryLens": {
                  "enableLspHover": false,
                  "hoverCancelGraceMs": 200
                }
              }
            }
            """);

        var config = LspClientConfiguration.FromConfigurationChangeRequest(token);

        Assert.False(config.EnableLspHover);
        Assert.Equal(200, config.HoverCancelGraceMs);
    }

    // ── Key aliasing: queryLens vs efQueryLens vs root ──────────────────────

    [Fact]
    public void FromToken_QueryLensKey_IsPreferred()
    {
        var token = JObject.Parse("""
            {
              "initializationOptions": {
                "queryLens": { "debugEnabled": true },
                "efQueryLens": { "debugEnabled": false }
              }
            }
            """);

        var config = LspClientConfiguration.FromInitializeRequest(token);

        Assert.True(config.DebugEnabled);
    }

    [Fact]
    public void FromToken_EfQueryLensKey_UsedWhenQueryLensAbsent()
    {
        var token = JObject.Parse("""
            {
              "initializationOptions": {
                "efQueryLens": { "debugEnabled": true }
              }
            }
            """);

        var config = LspClientConfiguration.FromInitializeRequest(token);

        Assert.True(config.DebugEnabled);
    }

    [Fact]
    public void FromToken_RootFallback_WhenNoKnownKey()
    {
        var token = JObject.Parse("""
            {
              "initializationOptions": {
                "debugEnabled": true,
                "hoverCacheTtlMs": 3000
              }
            }
            """);

        var config = LspClientConfiguration.FromInitializeRequest(token);

        Assert.True(config.DebugEnabled);
        Assert.Equal(3000, config.HoverCacheTtlMs);
    }

    // ── ReadBool parsing ────────────────────────────────────────────────────

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("False", false)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("YES", true)]
    [InlineData("ON", true)]
    public void ReadBool_StringValues_ParsedCorrectly(string value, bool expected)
    {
        var token = BuildSettings($"\"debugEnabled\": \"{value}\"");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.Equal(expected, config.DebugEnabled);
    }

    [Fact]
    public void ReadBool_NativeBoolean_ParsedCorrectly()
    {
        var token = BuildSettings("\"debugEnabled\": true");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.True(config.DebugEnabled);
    }

    [Fact]
    public void ReadBool_MissingProperty_ReturnsNull()
    {
        var token = BuildSettings("\"hoverCacheTtlMs\": 1000");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.Null(config.DebugEnabled);
    }

    [Fact]
    public void ReadBool_EmptyString_ReturnsNull()
    {
        var token = BuildSettings("\"debugEnabled\": \"\"");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.Null(config.DebugEnabled);
    }

    // ── ReadInt parsing ─────────────────────────────────────────────────────

    [Fact]
    public void ReadInt_InRange_ReturnsValue()
    {
        var token = BuildSettings("\"hoverCacheTtlMs\": 5000");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.Equal(5000, config.HoverCacheTtlMs);
    }

    [Fact]
    public void ReadInt_BelowMin_ClampsToMin()
    {
        var token = BuildSettings("\"hoverCacheTtlMs\": -1");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.Equal(0, config.HoverCacheTtlMs);
    }

    [Fact]
    public void ReadInt_AboveMax_ClampsToMax()
    {
        var token = BuildSettings("\"hoverCacheTtlMs\": 999999");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.Equal(120_000, config.HoverCacheTtlMs);
    }

    [Fact]
    public void ReadInt_FloatValue_RoundedToInt()
    {
        var token = BuildSettings("\"hoverCancelGraceMs\": 150.7");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.Equal(151, config.HoverCancelGraceMs);
    }

    [Fact]
    public void ReadInt_StringValue_Parsed()
    {
        var token = BuildSettings("\"hoverProgressDelayMs\": \"500\"");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.Equal(500, config.HoverProgressDelayMs);
    }

    [Fact]
    public void ReadInt_InvalidString_ReturnsNull()
    {
        var token = BuildSettings("\"hoverProgressDelayMs\": \"notanumber\"");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.Null(config.HoverProgressDelayMs);
    }

    [Fact]
    public void ReadInt_MissingProperty_ReturnsNull()
    {
        var token = BuildSettings("\"debugEnabled\": true");
        var config = LspClientConfiguration.FromInitializeRequest(token);
        Assert.Null(config.HoverCacheTtlMs);
    }

    // ── All properties round-trip ────────────────────────────────────────────

    [Fact]
    public void AllProperties_RoundTripFromToken()
    {
        var token = BuildSettings("""
            "debugEnabled": true,
            "enableLspHover": false,
            "hoverProgressNotify": true,
            "hoverProgressDelayMs": 100,
            "hoverCacheTtlMs": 8000,
            "hoverCancelGraceMs": 300,
            "markdownQueueAdaptiveWaitMs": 400,
            "structuredQueueAdaptiveWaitMs": 450,
            "warmupSuccessTtlMs": 60000,
            "warmupFailureCooldownMs": 10000
            """);

        var config = LspClientConfiguration.FromInitializeRequest(token);

        Assert.True(config.DebugEnabled);
        Assert.False(config.EnableLspHover);
        Assert.True(config.HoverProgressNotify);
        Assert.Equal(100, config.HoverProgressDelayMs);
        Assert.Equal(8000, config.HoverCacheTtlMs);
        Assert.Equal(300, config.HoverCancelGraceMs);
        Assert.Equal(400, config.MarkdownQueueAdaptiveWaitMs);
        Assert.Equal(450, config.StructuredQueueAdaptiveWaitMs);
        Assert.Equal(60000, config.WarmupSuccessTtlMs);
        Assert.Equal(10000, config.WarmupFailureCooldownMs);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static JObject BuildSettings(string properties) =>
        JObject.Parse($$"""
            {
              "initializationOptions": {
                "queryLens": { {{properties}} }
              }
            }
            """);
}
