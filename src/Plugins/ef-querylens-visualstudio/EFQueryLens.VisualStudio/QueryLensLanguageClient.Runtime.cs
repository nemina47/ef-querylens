// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;

internal sealed partial class QueryLensLanguageClient
{
    private static void ConfigureEnvironment(ProcessStartInfo processStartInfo, string workspaceRoot, string serverPath)
    {
        processStartInfo.Environment["QUERYLENS_WORKSPACE"] = workspaceRoot;
        processStartInfo.Environment["QUERYLENS_DAEMON_WORKSPACE"] = workspaceRoot;
        processStartInfo.Environment["QUERYLENS_DAEMON_START_TIMEOUT_MS"] = "30000";
        processStartInfo.Environment["QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS"] = "10000";
        // Keep daemon alive across VS language-client disposal to avoid UI teardown stalls.
        processStartInfo.Environment["QUERYLENS_DAEMON_SHUTDOWN_ON_DISPOSE"] = "0";
        // Keep rolling-window latency at 20 samples by default, but honor explicit env overrides.
        // Under .NET Framework, reading a missing key from ProcessStartInfo.Environment can throw.
        var avgWindowSamplesOverride = Environment.GetEnvironmentVariable("QUERYLENS_AVG_WINDOW_SAMPLES");
        if (string.IsNullOrWhiteSpace(avgWindowSamplesOverride))
        {
            processStartInfo.Environment["QUERYLENS_AVG_WINDOW_SAMPLES"] = "20";
        }
        processStartInfo.Environment["QUERYLENS_MAX_CODELENS_PER_DOCUMENT"] = DefaultMaxCodeLensPerDocument.ToString(System.Globalization.CultureInfo.InvariantCulture);
        processStartInfo.Environment["QUERYLENS_CODELENS_DEBOUNCE_MS"] = DefaultCodeLensDebounceMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        processStartInfo.Environment["QUERYLENS_CODELENS_USE_MODEL_FILTER"] = "0";
        processStartInfo.Environment["QUERYLENS_CLIENT"] = "vs";
        processStartInfo.Environment["QUERYLENS_DEBUG"] = "1";
        processStartInfo.Environment["QUERYLENS_ENABLE_LSP_HOVER"] = "0";
        var lspLogPath = BuildLspLogFilePath(workspaceRoot);
        processStartInfo.Environment["QUERYLENS_LSP_LOG_FILE"] = lspLogPath;
        currentLspLogPath = lspLogPath;
        var serverDirectory = Path.GetDirectoryName(serverPath) ?? string.Empty;
        var extensionRoot = Path.GetDirectoryName(serverDirectory) ?? serverDirectory;

        // Bundled daemon (from VSIX/local extension layout) only.
        var daemonExeCandidates = new List<string>
        {
            Path.Combine(extensionRoot, "daemon", "EFQueryLens.Daemon.exe"),
            Path.Combine(serverDirectory, "daemon", "EFQueryLens.Daemon.exe"),
            Path.Combine(serverDirectory, "EFQueryLens.Daemon.exe"),
        };
        var daemonDllCandidates = new List<string>
        {
            Path.Combine(extensionRoot, "daemon", "EFQueryLens.Daemon.dll"),
            Path.Combine(serverDirectory, "daemon", "EFQueryLens.Daemon.dll"),
            Path.Combine(serverDirectory, "EFQueryLens.Daemon.dll"),
        };

        foreach (var candidate in daemonExeCandidates)
        {
            if (!File.Exists(candidate)) continue;
            Log($"daemon-exe-resolved path={candidate}");
            processStartInfo.Environment["QUERYLENS_DAEMON_EXE"] = candidate;
            break;
        }

        foreach (var candidate in daemonDllCandidates)
        {
            if (!File.Exists(candidate)) continue;
            Log($"daemon-dll-resolved path={candidate}");
            processStartInfo.Environment["QUERYLENS_DAEMON_DLL"] = candidate;
            break;
        }
    }

    private static async Task PumpServerErrorStreamAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (line.Length > 0)
                {
                    Log($"lsp-stderr pid={process.Id} {line}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation during shutdown.
        }
        catch (Exception ex)
        {
            Log($"lsp-stderr-pump-failed type={ex.GetType().Name} message={ex.Message}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [INFO] pid={Process.GetCurrentProcess().Id} {message}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, line);
        }
        catch
        {
            // Best effort only.
        }
    }

    private void DisposeCore(string reason)
    {
        if (Interlocked.Exchange(ref disposeRequested, 1) == 1)
        {
            return;
        }

        CancellationTokenSource? errorPumpCts;
        JsonRpc? rpcToDispose;
        Process? processToDispose;

        lock (Sync)
        {
            serverErrorPumpTask = null;
            errorPumpCts = serverErrorPumpCts;
            serverErrorPumpCts = null;
            rpcToDispose = rpc;
            rpc = null;
            processToDispose = serverProcess;
            serverProcess = null;
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        }

        try
        {
            errorPumpCts?.Cancel();
        }
        catch
        {
            // Best effort only.
        }

        try
        {
            rpcToDispose?.Dispose();
        }
        catch
        {
            // Best effort only.
        }

        if (processToDispose is not null)
        {
            try
            {
                if (!processToDispose.HasExited)
                {
                    processToDispose.Kill();
                }
            }
            catch
            {
                // Best effort only.
            }

            try
            {
                processToDispose.Dispose();
            }
            catch
            {
                // Best effort only.
            }
        }

        try
        {
            errorPumpCts?.Dispose();
        }
        catch
        {
            // Best effort only.
        }

        Log($"language-client-disposed reason={reason}");
    }
}
