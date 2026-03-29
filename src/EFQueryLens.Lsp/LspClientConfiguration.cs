using Newtonsoft.Json.Linq;

namespace EFQueryLens.Lsp;

internal sealed record LspClientConfiguration(
    bool? DebugEnabled = null,
    bool? EnableLspHover = null,
    bool? HoverProgressNotify = null,
    int? HoverProgressDelayMs = null,
    int? HoverCacheTtlMs = null,
    int? MarkdownQueueAdaptiveWaitMs = null,
    int? StructuredQueueAdaptiveWaitMs = null,
    int? WarmupSuccessTtlMs = null,
    int? WarmupFailureCooldownMs = null)
{
    internal static LspClientConfiguration FromInitializeRequest(JToken? initializeRequest)
    {
        return FromToken(initializeRequest?["initializationOptions"]);
    }

    internal static LspClientConfiguration FromConfigurationChangeRequest(JToken? configurationChangeRequest)
    {
        return FromToken(configurationChangeRequest?["settings"]);
    }

    private static LspClientConfiguration FromToken(JToken? token)
    {
        if (token is null)
        {
            return new LspClientConfiguration();
        }

        var root = token["queryLens"] ?? token["efQueryLens"] ?? token;
        if (root is not JObject config)
        {
            return new LspClientConfiguration();
        }

        return new LspClientConfiguration(
            DebugEnabled: ReadBool(config, "debugEnabled"),
            EnableLspHover: ReadBool(config, "enableLspHover"),
            HoverProgressNotify: ReadBool(config, "hoverProgressNotify"),
            HoverProgressDelayMs: ReadInt(config, "hoverProgressDelayMs", min: 0, max: 5_000),
            HoverCacheTtlMs: ReadInt(config, "hoverCacheTtlMs", min: 0, max: 120_000),
            MarkdownQueueAdaptiveWaitMs: ReadInt(config, "markdownQueueAdaptiveWaitMs", min: 0, max: 2_000),
            StructuredQueueAdaptiveWaitMs: ReadInt(config, "structuredQueueAdaptiveWaitMs", min: 0, max: 2_000),
            WarmupSuccessTtlMs: ReadInt(config, "warmupSuccessTtlMs", min: 0, max: 600_000),
            WarmupFailureCooldownMs: ReadInt(config, "warmupFailureCooldownMs", min: 0, max: 120_000));
    }

    private static bool? ReadBool(JObject config, string propertyName)
    {
        var token = config[propertyName];
        if (token is null)
        {
            return null;
        }

        if (token.Type == JTokenType.Boolean)
        {
            return token.Value<bool>();
        }

        var raw = token.Value<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ReadInt(JObject config, string propertyName, int min, int max)
    {
        var token = config[propertyName];
        if (token is null)
        {
            return null;
        }

        var value = token.Type switch
        {
            JTokenType.Integer => token.Value<int>(),
            JTokenType.Float => (int)Math.Round(token.Value<double>()),
            _ => int.TryParse(token.Value<string>(), out var parsed) ? parsed : (int?)null,
        };

        if (!value.HasValue)
        {
            return null;
        }

        if (value.Value < min)
        {
            return min;
        }

        if (value.Value > max)
        {
            return max;
        }

        return value.Value;
    }
}
