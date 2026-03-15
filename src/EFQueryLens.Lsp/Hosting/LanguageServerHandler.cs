using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace EFQueryLens.Lsp.Hosting;

/// <summary>
/// All LSP method handlers registered with <see cref="JsonRpc"/>.
/// Methods returning <c>void</c> are notification handlers (no response).
/// Methods returning <c>Task&lt;T&gt;</c> are request handlers (response required).
/// StreamJsonRpc dispatches based on the <see cref="JsonRpcMethodAttribute"/> wire name.
/// </summary>
internal sealed partial class LanguageServerHandler
{
    private readonly HoverHandler _hover;
    private readonly WarmupHandler _warmup;
    private readonly DaemonControlHandler _daemonControl;
    private readonly TextDocumentSyncHandler _textSync;
    private readonly bool _debugEnabled;
    private readonly bool _hoverProgressEnabled;
    private readonly int _hoverProgressDelayMs;
    private bool _shutdownRequested;

    /// <summary>
    /// Set immediately after <see cref="JsonRpc"/> construction so that
    /// <see cref="Exit"/> can dispose the connection.
    /// </summary>
    internal JsonRpc? JsonRpc { get; set; }

    public LanguageServerHandler(
        HoverHandler hover,
        WarmupHandler warmup,
        DaemonControlHandler daemonControl,
        TextDocumentSyncHandler textSync,
        bool debugEnabled = false)
    {
        _hover = hover;
        _warmup = warmup;
        _daemonControl = daemonControl;
        _textSync = textSync;
        _debugEnabled = debugEnabled;
        _hoverProgressEnabled = ReadBoolEnvironmentVariable(
            "QUERYLENS_HOVER_PROGRESS_NOTIFY",
            fallback: false);
        _hoverProgressDelayMs = ReadIntEnvironmentVariable(
            "QUERYLENS_HOVER_PROGRESS_DELAY_MS",
            fallback: 350,
            min: 0,
            max: 5_000);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public JObject Initialize(JToken? _ = null) => CreateInitializeResult();

    [JsonRpcMethod("initialized")]
    public void Initialized() { }

    [JsonRpcMethod("shutdown")]
    public void Shutdown() => _shutdownRequested = true;

    [JsonRpcMethod("exit")]
    public void Exit()
    {
        if (!_shutdownRequested)
            Environment.ExitCode = 1;
        JsonRpc?.Dispose();
    }

    private static int ReadIntEnvironmentVariable(string variableName, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (!int.TryParse(raw, out var value))
        {
            return fallback;
        }

        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static bool ReadBoolEnvironmentVariable(string variableName, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static JObject CreateInitializeResult()
    {
        var enableLspHover = ReadBoolEnvironmentVariable("QUERYLENS_ENABLE_LSP_HOVER", fallback: true);

        return new JObject
        {
            ["capabilities"] = new JObject
            {
                ["textDocumentSync"] = new JObject
                {
                    ["openClose"] = true,
                    ["change"] = (int)TextDocumentSyncKind.Full,
                    ["save"] = new JObject { ["includeText"] = true },
                },
                ["hoverProvider"] = enableLspHover,
                ["executeCommandProvider"] = new JObject
                {
                    ["commands"] = new JArray(
                        "efquerylens.warmup",
                        "efquerylens.daemon.restart",
                        "efquerylens.preview.recalculate",
                        "efquerylens.preview.structuredHover")
                },
            },
        };
    }
}
