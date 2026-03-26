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
                cancellationToken);

            var success = response?["success"]?.Value<bool>() == true;
            var message = response?["message"]?.Value<string>()
                ?? (success ? "Daemon restarted." : "Daemon restart did not complete.");
            var code = success
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
                cancellationToken);

            var success = response?["success"]?.Value<bool>() == true;
            var message = response?["message"]?.Value<string>()
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
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(currentLspLogPath))
        {
            result.Add(currentLspLogPath!);
        }

        result.Add(LogFilePath);
        return result;
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
            var uri = new Uri(filePath).AbsoluteUri;
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
                cancellationToken);

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
