// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

internal sealed partial class QueryLensLanguageClient
{
    internal static async Task<(bool Success, string Code, string Message)> RequestDaemonRestartAsync(CancellationToken cancellationToken)
    {
        var client = Current;
        if (client is null)
        {
            return (false, QueryLensErrorCodes.DaemonRestartClientNotReady, "Language client is not active yet.");
        }

        var languageServerRpc = client.rpc;
        if (languageServerRpc is null)
        {
            return (false, QueryLensErrorCodes.DaemonRestartRpcNotReady, "Language server RPC channel is not ready yet.");
        }

        try
        {
            var response = await languageServerRpc.InvokeWithParameterObjectAsync<JToken?>(
                "efquerylens/daemon/restart",
                new JObject(),
                cancellationToken).ConfigureAwait(false);

            bool success = response?["success"]?.Value<bool>() == true;
            string message = response?["message"]?.Value<string>()
                ?? (success ? "Daemon restarted." : "Daemon restart did not complete.");
            string code = success
                ? "OK"
                : QueryLensErrorCodes.DaemonRestartIncomplete;
            return (success, code, message);
        }
        catch (Exception ex)
        {
            Log($"daemon-restart-request-failed code={QueryLensErrorCodes.DaemonRestartFailed} type={ex.GetType().Name} message={ex.Message}");
            return (false, QueryLensErrorCodes.DaemonRestartFailed, $"Daemon restart failed: {ex.Message}");
        }
    }

    internal static async Task<(bool Success, string Message)> RequestPreviewRecalculateAsync(
        string documentUri,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var client = Current;
        if (client?.rpc is not JsonRpc languageServerRpc)
        {
            return (false, "Language server RPC channel is not ready yet.");
        }

        try
        {
            var response = await languageServerRpc.InvokeWithParameterObjectAsync<JToken?>(
                "efquerylens/preview/recalculate",
                new JObject
                {
                    ["textDocument"] = new JObject
                    {
                        ["uri"] = documentUri,
                    },
                    ["position"] = new JObject
                    {
                        ["line"] = line,
                        ["character"] = character,
                    },
                },
                cancellationToken).ConfigureAwait(false);

            bool success = response?["success"]?.Value<bool>() == true;
            string message = response?["message"]?.Value<string>()
                ?? (success ? "Preview recalculated." : "Preview recalculation did not complete.");
            return (success, message);
        }
        catch (Exception ex)
        {
            Log($"preview-recalculate-request-failed type={ex.GetType().Name} message={ex.Message}");
            return (false, $"Preview recalculation failed: {ex.Message}");
        }
    }

    internal static IReadOnlyList<string> GetLogFilePaths()
    {
        List<string> result = new();
        if (!string.IsNullOrWhiteSpace(currentLspLogPath))
        {
            result.Add(currentLspLogPath!);
        }

        result.Add(LogFilePath);
        return result;
    }

    /// <summary>
    /// Notifies the LSP server of a document being opened.
    /// </summary>
    internal static void NotifyDocumentOpened(string filePath, string sourceText)
    {
        if (Current?.rpc is not JsonRpc rpc)
        {
            return;
        }

        try
        {
            string uri = new Uri(filePath).AbsoluteUri;
            _ = rpc.NotifyWithParameterObjectAsync(
                "textDocument/didOpen",
                new JObject
                {
                    ["textDocument"] = new JObject
                    {
                        ["uri"] = uri,
                        ["languageId"] = "csharp",
                        ["version"] = 1,
                        ["text"] = sourceText,
                    }
                });
        }
        catch (Exception ex)
        {
            Log($"document-open-notify-failed path={Path.GetFileName(filePath)} type={ex.GetType().Name} message={ex.Message}");
        }
    }

    /// <summary>
    /// Notifies the LSP server of document content changes.
    /// </summary>
    internal static void NotifyDocumentChanged(string filePath, string sourceText)
    {
        if (Current?.rpc is not JsonRpc rpc)
        {
            return;
        }

        try
        {
            string uri = new Uri(filePath).AbsoluteUri;
            _ = rpc.NotifyWithParameterObjectAsync(
                "textDocument/didChange",
                new JObject
                {
                    ["textDocument"] = new JObject
                    {
                        ["uri"] = uri,
                        ["version"] = 2,
                    },
                    ["contentChanges"] = new JArray
                    {
                        new JObject
                        {
                            ["text"] = sourceText,
                        }
                    }
                });
        }
        catch (Exception ex)
        {
            Log($"document-change-notify-failed path={Path.GetFileName(filePath)} type={ex.GetType().Name} message={ex.Message}");
        }
    }

    /// <summary>
    /// Notifies the LSP server of a document being closed.
    /// </summary>
    internal static void NotifyDocumentClosed(string filePath)
    {
        if (Current?.rpc is not JsonRpc rpc)
        {
            return;
        }

        try
        {
            string uri = new Uri(filePath).AbsoluteUri;
            _ = rpc.NotifyWithParameterObjectAsync(
                "textDocument/didClose",
                new JObject
                {
                    ["textDocument"] = new JObject
                    {
                        ["uri"] = uri,
                    }
                });
        }
        catch (Exception ex)
        {
            Log($"document-close-notify-failed path={Path.GetFileName(filePath)} type={ex.GetType().Name} message={ex.Message}");
        }
    }

    internal static async Task<QueryLensStructuredHoverResponse?> TryGetStructuredHoverAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var client = Current;
        if (client is null) return null;
        var languageServerRpc = client.rpc;
        if (languageServerRpc is null) return null;

        try
        {
            Log($"structured-hover-request-start file={Path.GetFileName(filePath)} line={line} char={character}");
            string uri = new Uri(filePath).AbsoluteUri;
            var response = await languageServerRpc.InvokeWithParameterObjectAsync<JToken?>(
                "efquerylens/hover",
                new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri },
                    ["position"] = new JObject
                    {
                        ["line"] = Math.Max(0, line),
                        ["character"] = Math.Max(0, character),
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (response is null || response.Type == JTokenType.Null)
            {
                Log($"structured-hover-request-null file={Path.GetFileName(filePath)} line={line} char={character}");
                return null;
            }

            var result = response.ToObject<QueryLensStructuredHoverResponse>();
            Log($"structured-hover-request-success file={Path.GetFileName(filePath)} line={line} char={character} success={result?.Success}");
            return result;
        }
        catch (Exception ex)
        {
            Log($"structured-hover-request-failed type={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }
}
