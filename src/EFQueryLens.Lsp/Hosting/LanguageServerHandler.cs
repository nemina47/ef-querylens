using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp;
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
    private bool _debugEnabled;
    private bool _hoverProgressEnabled;
    private int _hoverProgressDelayMs;
    private bool _enableLspHover;
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
        _hoverProgressEnabled = LspEnvironment.ReadBool(
            "QUERYLENS_HOVER_PROGRESS_NOTIFY",
            fallback: false);
        _hoverProgressDelayMs = LspEnvironment.ReadInt(
            "QUERYLENS_HOVER_PROGRESS_DELAY_MS",
            fallback: 350,
            min: 0,
            max: 5_000);
            _enableLspHover = LspEnvironment.ReadBool("QUERYLENS_ENABLE_LSP_HOVER", fallback: true);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public JObject Initialize(JToken? request = null)
    {
        var configuration = LspClientConfiguration.FromInitializeRequest(request);
        ApplyClientConfiguration(configuration);

        return CreateInitializeResult(_enableLspHover);
    }

    [JsonRpcMethod("initialized")]
    public void Initialized() { }

    [JsonRpcMethod("workspace/didChangeConfiguration", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeConfiguration(JToken? request = null)
    {
        var configuration = LspClientConfiguration.FromConfigurationChangeRequest(request);
        ApplyClientConfiguration(configuration);
        _hover.InvalidateForConfigurationChange();
    }

    [JsonRpcMethod("shutdown")]
    public void Shutdown() => _shutdownRequested = true;

    [JsonRpcMethod("exit")]
    public void Exit()
    {
        if (!_shutdownRequested)
            Environment.ExitCode = 1;
        JsonRpc?.Dispose();
    }

    private void ApplyClientConfiguration(LspClientConfiguration configuration)
    {
        if (configuration.DebugEnabled.HasValue)
        {
            _debugEnabled = configuration.DebugEnabled.Value;
        }

        if (configuration.HoverProgressNotify.HasValue)
        {
            _hoverProgressEnabled = configuration.HoverProgressNotify.Value;
        }

        if (configuration.HoverProgressDelayMs.HasValue)
        {
            _hoverProgressDelayMs = configuration.HoverProgressDelayMs.Value;
        }

        if (configuration.EnableLspHover.HasValue)
        {
            _enableLspHover = configuration.EnableLspHover.Value;
        }

        _hover.ApplyClientConfiguration(configuration);
        _warmup.ApplyClientConfiguration(configuration);
        _daemonControl.ApplyClientConfiguration(configuration);

        if (_debugEnabled)
        {
            Console.Error.WriteLine(
                $"[QL-LSP] initialize-options applied " +
                $"hoverEnabled={_enableLspHover} progress={_hoverProgressEnabled} " +
                $"progressDelayMs={_hoverProgressDelayMs}");
        }
    }

    private static JObject CreateInitializeResult(bool enableLspHover)
    {
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
                ["codeLensProvider"] = new JObject
                {
                    ["resolveProvider"] = false
                },
                ["executeCommandProvider"] = new JObject
                {
                    ["commands"] = new JArray(
                        "efquerylens.warmup",
                        "efquerylens.daemon.restart",
                        "efquerylens.preview.recalculate",
                        "efquerylens.preview.structuredHover",
                        "efquerylens.showsqlpopup",
                        "efquerylens.opensqleditor",
                        "efquerylens.copysql",
                        "efquerylens.reanalyze")
                },
            },
        };
    }
}
