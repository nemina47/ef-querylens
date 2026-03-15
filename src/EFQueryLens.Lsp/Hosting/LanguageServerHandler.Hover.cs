using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace EFQueryLens.Lsp.Hosting;

internal sealed partial class LanguageServerHandler
{
    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover?> HoverAsync(TextDocumentPositionParams request, CancellationToken ct)
    {
        if (_debugEnabled) Console.Error.WriteLine("[QL-LSP] request method=textDocument/hover");

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

    [JsonRpcMethod("efquerylens/hover", UseSingleObjectParameterDeserialization = true)]
    public Task<QueryLensStructuredHoverResult?> StructuredHoverAsync(TextDocumentPositionParams request, CancellationToken ct)
    {
        if (_debugEnabled) Console.Error.WriteLine("[QL-LSP] request method=efquerylens/hover");
        return _hover.HandleStructuredAsync(request, ct);
    }

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
}
