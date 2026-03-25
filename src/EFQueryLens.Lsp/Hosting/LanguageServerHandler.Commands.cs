using EFQueryLens.Lsp.Handlers;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace EFQueryLens.Lsp.Hosting;

internal sealed partial class LanguageServerHandler
{
    [JsonRpcMethod("efquerylens/warmup", UseSingleObjectParameterDeserialization = true)]
    public Task<WarmupResponse> WarmupAsync(TextDocumentPositionParams request, CancellationToken ct)
    {
        if (_debugEnabled) Console.Error.WriteLine("[QL-LSP] request method=efquerylens/warmup");
        return _warmup.HandleAsync(request, ct);
    }

    [JsonRpcMethod("efquerylens/daemon/restart", UseSingleObjectParameterDeserialization = true)]
    public Task<DaemonRestartResponse> RestartDaemonAsync(JToken? _ = null, CancellationToken ct = default)
    {
        if (_debugEnabled) Console.Error.WriteLine("[QL-LSP] request method=efquerylens/daemon/restart");
        return _daemonControl.RestartAsync(ct);
    }

    [JsonRpcMethod("efquerylens/preview/recalculate", UseSingleObjectParameterDeserialization = true)]
    public async Task<JObject> RecalculatePreviewAsync(TextDocumentPositionParams request, CancellationToken ct)
    {
        if (_debugEnabled) Console.Error.WriteLine("[QL-LSP] request method=efquerylens/preview/recalculate");

        var invalidateResponse = await _daemonControl.InvalidateQueryCachesAsync(ct);
        if (!invalidateResponse.Success)
        {
            return new JObject
            {
                ["success"] = false,
                ["message"] = invalidateResponse.Message,
                ["removedCachedResults"] = invalidateResponse.RemovedCachedResults,
                ["removedInflightJobs"] = invalidateResponse.RemovedInflightJobs,
            };
        }

        _hover.InvalidateForManualRecalculate();
        var hover = await _hover.HandleStructuredAsync(request, ct);

        return new JObject
        {
            ["success"] = true,
            ["message"] = "Preview cache invalidated and query recalculated.",
            ["removedCachedResults"] = invalidateResponse.RemovedCachedResults,
            ["removedInflightJobs"] = invalidateResponse.RemovedInflightJobs,
            ["hover"] = hover is null ? null : JObject.FromObject(hover),
        };
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

        if (command.Equals("efquerylens.preview.recalculate", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = request["arguments"] as JArray;
            var recalculateRequest = arguments?.Count > 0
                ? arguments[0].ToObject<TextDocumentPositionParams>()
                : null;

            if (recalculateRequest is null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["message"] = "Missing or invalid recalculate request payload.",
                };
            }

            return await RecalculatePreviewAsync(recalculateRequest, ct);
        }

        if (command.Equals("efquerylens.preview.structuredHover", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = request["arguments"] as JArray;
            var structuredHoverRequest = arguments?.Count > 0
                ? arguments[0].ToObject<TextDocumentPositionParams>()
                : null;

            if (structuredHoverRequest is null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["message"] = "Missing or invalid structured hover request payload.",
                };
            }

            var hover = await _hover.HandleStructuredAsync(structuredHoverRequest, ct);
            return new JObject
            {
                ["success"] = hover is not null,
                ["message"] = hover?.ErrorMessage,
                ["hover"] = hover is null ? null : JObject.FromObject(hover),
            };
        }

        if (command.Equals("efquerylens.showsqlpopup", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = request["arguments"] as JArray;
            var req = arguments?.Count > 0
                ? arguments[0].ToObject<TextDocumentPositionParams>()
                : null;

            if (req is null) return new JObject { ["success"] = false };

            var hover = await _hover.HandleStructuredAsync(req, ct);
            if (hover is not null)
            {
                _ = JsonRpc?.NotifyAsync("efquerylens/showSqlPopup", new JObject
                {
                    ["hover"] = JObject.FromObject(hover),
                    ["fallbackFileUri"] = req.TextDocument.Uri.ToString(),
                    ["fallbackLine"] = req.Position.Line
                });
            }

            return new JObject { ["success"] = true };
        }

        if (command.Equals("efquerylens.opensqleditor", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = request["arguments"] as JArray;
            var req = arguments?.Count > 0
                ? arguments[0].ToObject<TextDocumentPositionParams>()
                : null;

            if (req is null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["message"] = "Missing or invalid payload.",
                };
            }

            var hover = await _hover.HandleStructuredAsync(req, ct);
            if (hover is not null)
            {
                var payload = new JObject
                {
                    ["hover"] = JObject.FromObject(hover),
                    ["fallbackFileUri"] = req.TextDocument.Uri.ToString(),
                    ["fallbackLine"] = req.Position.Line
                };
                
                _ = JsonRpc?.NotifyAsync("efquerylens/showSqlPreview", payload);
            }

            return new JObject { ["success"] = true };
        }

        if (command.Equals("efquerylens.copysql", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = request["arguments"] as JArray;
            var req = arguments?.Count > 0
                ? arguments[0].ToObject<TextDocumentPositionParams>()
                : null;

            if (req is null) return new JObject { ["success"] = false };

            var hover = await _hover.HandleStructuredAsync(req, ct);
            if (hover is not null)
            {
                _ = JsonRpc?.NotifyAsync("efquerylens/copySqlToClipboard", new JObject
                {
                    ["sql"] = hover.EnrichedSql ?? string.Join("\n\n", hover.Statements.Select(s => s.Sql))
                });
            }

            return new JObject { ["success"] = true };
        }

        if (command.Equals("efquerylens.reanalyze", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = request["arguments"] as JArray;
            var req = arguments?.Count > 0
                ? arguments[0].ToObject<TextDocumentPositionParams>()
                : null;

            if (req is null) return new JObject { ["success"] = false };

            await RecalculatePreviewAsync(req, ct);
            return new JObject { ["success"] = true };
        }

        return new JObject
        {
            ["success"] = false,
            ["message"] = $"Unsupported command '{command}'.",
        };
    }
}
