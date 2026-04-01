using EFQueryLens.Lsp;
using Newtonsoft.Json.Linq;

namespace EFQueryLens.Core.Tests.Lsp;

public class LspClientConfigurationTests
{
    [Fact]
    public void FromInitializeRequest_ReadsQueryLensSettings_AndClampsInts()
    {
        var request = JObject.Parse("""
            {
              "initializationOptions": {
                "queryLens": {
                  "debugEnabled": "yes",
                  "enableLspHover": false,
                  "hoverProgressNotify": "on",
                  "hoverProgressDelayMs": -20,
                  "hoverCacheTtlMs": 999999,
                  "markdownQueueAdaptiveWaitMs": 5.6,
                  "structuredQueueAdaptiveWaitMs": "9",
                  "warmupSuccessTtlMs": 700000,
                  "warmupFailureCooldownMs": -1
                }
              }
            }
            """);

        var config = LspClientConfiguration.FromInitializeRequest(request);

        Assert.True(config.DebugEnabled);
        Assert.False(config.EnableLspHover);
        Assert.True(config.HoverProgressNotify);
        Assert.Equal(0, config.HoverProgressDelayMs);
        Assert.Equal(120000, config.HoverCacheTtlMs);
        Assert.Equal(6, config.MarkdownQueueAdaptiveWaitMs);
        Assert.Equal(9, config.StructuredQueueAdaptiveWaitMs);
        Assert.Equal(600000, config.WarmupSuccessTtlMs);
        Assert.Equal(0, config.WarmupFailureCooldownMs);
    }

    [Fact]
    public void FromConfigurationChangeRequest_ReadsEfQueryLensRoot()
    {
        var request = JObject.Parse("""
            {
              "settings": {
                "efQueryLens": {
                  "debugEnabled": true,
                  "enableLspHover": true,
                  "hoverProgressNotify": false
                }
              }
            }
            """);

        var config = LspClientConfiguration.FromConfigurationChangeRequest(request);

        Assert.True(config.DebugEnabled);
        Assert.True(config.EnableLspHover);
        Assert.False(config.HoverProgressNotify);
    }

    [Fact]
    public void FromInitializeRequest_WithMissingOrInvalidToken_ReturnsDefaults()
    {
        var empty = LspClientConfiguration.FromInitializeRequest(null);
        Assert.Null(empty.DebugEnabled);

      Assert.Throws<ArgumentException>(() =>
        LspClientConfiguration.FromInitializeRequest(JObject.Parse("{" + "\"initializationOptions\": []}")));
    }
}
