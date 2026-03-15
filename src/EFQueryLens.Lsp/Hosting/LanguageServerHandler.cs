using EFQueryLens.Lsp.Handlers;
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
internal sealed class LanguageServerHandler
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

    // ── Text document sync (notifications) ───────────────────────────────────

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(DidOpenTextDocumentParams p) => _textSync.DidOpen(p);

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams p) => _textSync.DidChange(p);

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams p) => _textSync.DidClose(p);

    [JsonRpcMethod("textDocument/didSave", UseSingleObjectParameterDeserialization = true)]
    public void DidSave(DidSaveTextDocumentParams p) => _textSync.DidSave(p);

    [JsonRpcMethod("workspace/didChangeConfiguration", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeConfiguration(JToken? _ = null) { }

    // ── Hover ─────────────────────────────────────────────────────────────────

    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover?> HoverAsync(TextDocumentPositionParams request, CancellationToken ct)
    {
        if (_debugEnabled) Console.Error.WriteLine($"[QL-LSP] request method=textDocument/hover");

        if (!_hoverProgressEnabled || JsonRpc is null)
        {
            return await _hover.HandleAsync(request, ct);
        }

        var hoverTask = _hover.HandleAsync(request, ct);
        if (hoverTask.IsCompleted)
        {
            return await hoverTask;
        }

        using var delayCts = new CancellationTokenSource();
        var delayTask = Task.Delay(_hoverProgressDelayMs, delayCts.Token);
        var winner = await Task.WhenAny(hoverTask, delayTask);
        if (winner == hoverTask)
        {
            delayCts.Cancel();
            return await hoverTask;
        }

        var progressToken = Guid.NewGuid().ToString("N");
        var progressStarted = await TryStartHoverProgressAsync(progressToken);

        try
        {
            return await hoverTask;
        }
        finally
        {
            if (progressStarted)
            {
                await TryEndHoverProgressAsync(progressToken, ct.IsCancellationRequested
                    ? "SQL preview canceled."
                    : "SQL preview ready.");
            }
        }
    }

    [JsonRpcMethod("efquerylens/warmup", UseSingleObjectParameterDeserialization = true)]
    public Task<WarmupResponse> WarmupAsync(TextDocumentPositionParams request, CancellationToken ct)
    {
        if (_debugEnabled) Console.Error.WriteLine($"[QL-LSP] request method=efquerylens/warmup");
        return _warmup.HandleAsync(request, ct);
    }

    [JsonRpcMethod("efquerylens/daemon/restart", UseSingleObjectParameterDeserialization = true)]
    public Task<DaemonRestartResponse> RestartDaemonAsync(JToken? _ = null, CancellationToken ct = default)
    {
        if (_debugEnabled) Console.Error.WriteLine("[QL-LSP] request method=efquerylens/daemon/restart");
        return _daemonControl.RestartAsync(ct);
    }

    [JsonRpcMethod("workspace/executeCommand", UseSingleObjectParameterDeserialization = true)]
    public async Task<JToken?> ExecuteCommandAsync(JObject request, CancellationToken ct)
    {
        var command = request["command"]?.Value<string>();
        if (_debugEnabled) Console.Error.WriteLine($"[QL-LSP] request method=workspace/executeCommand command={command ?? "<null>"}");

        if (string.IsNullOrWhiteSpace(command))
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = "Missing command.",
            };
        }

        if (command.Equals("efquerylens.warmup", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = request["arguments"] as JArray;
            var warmupRequest = arguments?.Count > 0
                ? arguments[0].ToObject<TextDocumentPositionParams>()
                : null;

            if (warmupRequest is null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["message"] = "Missing or invalid warmup request payload.",
                };
            }

            var warmupResponse = await _warmup.HandleAsync(warmupRequest, ct);
            return JObject.FromObject(warmupResponse);
        }

        if (command.Equals("efquerylens.daemon.restart", StringComparison.OrdinalIgnoreCase))
        {
            var restartResponse = await _daemonControl.RestartAsync(ct);
            return JObject.FromObject(restartResponse);
        }

        return new JObject
        {
            ["success"] = false,
            ["message"] = $"Unsupported command '{command}'.",
        };
    }

    // ── Server capabilities ──────────────────────────────────────────────────

    private async Task<bool> TryStartHoverProgressAsync(string progressToken)
    {
        var rpc = JsonRpc;
        if (rpc is null)
        {
            return false;
        }

        try
        {
            await rpc.InvokeWithParameterObjectAsync<JToken?>(
                "window/workDoneProgress/create",
                new JObject { ["token"] = progressToken },
                CancellationToken.None);

            await rpc.NotifyAsync(
                "$/progress",
                new JObject
                {
                    ["token"] = progressToken,
                    ["value"] = new JObject
                    {
                        ["kind"] = "begin",
                        ["title"] = "EF QueryLens",
                        ["message"] = "Processing SQL preview...",
                        ["cancellable"] = false,
                    }
                });

            return true;
        }
        catch (Exception ex)
        {
            if (_debugEnabled)
            {
                Console.Error.WriteLine($"[QL-LSP] hover-progress-start failed type={ex.GetType().Name} message={ex.Message}");
            }

            return false;
        }
    }

    private async Task TryEndHoverProgressAsync(string progressToken, string message)
    {
        var rpc = JsonRpc;
        if (rpc is null)
        {
            return;
        }

        try
        {
            await rpc.NotifyAsync(
                "$/progress",
                new JObject
                {
                    ["token"] = progressToken,
                    ["value"] = new JObject
                    {
                        ["kind"] = "end",
                        ["message"] = message,
                    }
                });
        }
        catch (Exception ex)
        {
            if (_debugEnabled)
            {
                Console.Error.WriteLine($"[QL-LSP] hover-progress-end failed type={ex.GetType().Name} message={ex.Message}");
            }
        }
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
                        "efquerylens.daemon.restart")
                },
            },
        };
    }
}
