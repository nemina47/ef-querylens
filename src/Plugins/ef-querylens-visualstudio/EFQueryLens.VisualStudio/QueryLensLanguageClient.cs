// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

[Export(typeof(ILanguageClient))]
[ContentType("CSharp")]
[Name("EF QueryLens")]
internal sealed class QueryLensLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
{
    private const int DefaultMaxCodeLensPerDocument = 50;
    private const int DefaultCodeLensDebounceMilliseconds = 250;
    private const string LspDllOverrideEnvVar = "QUERYLENS_LSP_DLL";
    private const string RepositoryRootOverrideEnvVar = "QUERYLENS_REPOSITORY_ROOT";
    private static readonly object Sync = new();
    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "EFQueryLens.VisualStudio.log");
    private static string? currentLspLogPath;

    private Process? serverProcess;
    private JsonRpc? rpc;

    internal static QueryLensLanguageClient? Current { get; private set; }

    public QueryLensLanguageClient()
    {
        lock (Sync)
        {
            Current = this;
        }
    }

    public string Name => "EF QueryLens";

    public IEnumerable<string> ConfigurationSections => [];

    public object? InitializationOptions => null;

    public IEnumerable<string> FilesToWatch => [];

    public bool ShowNotificationOnInitializeFailed => true;

    public object CustomMessageTarget => null!;

    public object MiddleLayer => null!;

#pragma warning disable CS0067
    public event AsyncEventHandler<EventArgs>? StartAsync;

    public event AsyncEventHandler<EventArgs>? StopAsync;
#pragma warning restore CS0067

    public async Task<Connection?> ActivateAsync(CancellationToken cancellationToken)
    {
        var extensionDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Unable to resolve extension assembly directory.");

        var workspaceRoot = ResolveWorkspacePath(extensionDirectory);
        var serverPath = ResolveServerPath(extensionDirectory, workspaceRoot);
        Log($"lsp-server-path-resolved extensionDir={extensionDirectory} workspaceRoot={workspaceRoot} serverPath={serverPath} exists={File.Exists(serverPath)}");
        if (!File.Exists(serverPath))
        {
            Log($"lsp-server-not-found path={serverPath}");
            throw new FileNotFoundException("Could not find the QueryLens language server assembly.", serverPath);
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{serverPath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspaceRoot,
        };

        ConfigureEnvironment(processStartInfo, workspaceRoot, serverPath);

        serverProcess = new Process { StartInfo = processStartInfo };
        if (!serverProcess.Start())
        {
            throw new InvalidOperationException("Failed to start QueryLens language server process.");
        }

        Log($"lsp-process-started pid={serverProcess.Id} path={serverPath} workspace={workspaceRoot}");
        _ = PumpServerErrorStreamAsync(serverProcess, cancellationToken);

        await Task.Yield();
        return new Connection(serverProcess.StandardOutput.BaseStream, serverProcess.StandardInput.BaseStream);
    }

    public async Task OnLoadedAsync()
    {
        Log("language-client-loaded");
        await (StartAsync?.InvokeAsync(this, EventArgs.Empty) ?? Task.CompletedTask);
    }

    public Task OnServerInitializedAsync()
    {
        Log("language-server-initialized");
        return Task.CompletedTask;
    }

    public Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
    {
        Log($"language-server-init-failed state={initializationState}");
        return Task.FromResult<InitializationFailureContext?>(null);
    }

    public Task AttachForCustomMessageAsync(JsonRpc rpc)
    {
        this.rpc = rpc;
        Log("custom-message-rpc-attached");
        return Task.CompletedTask;
    }

    internal static async Task<(bool Success, string Message)> RequestDaemonRestartAsync(CancellationToken cancellationToken)
    {
        var client = Current;
        if (client is null)
        {
            return (false, "Language client is not active yet.");
        }

        var languageServerRpc = client.rpc;
        if (languageServerRpc is null)
        {
            return (false, "Language server RPC channel is not ready yet.");
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

            return (success, message);
        }
        catch (Exception ex)
        {
            Log($"daemon-restart-request-failed type={ex.GetType().Name} message={ex.Message}");
            return (false, $"Daemon restart failed: {ex.Message}");
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

    internal static async Task<string?> TryGetHoverMarkdownAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var client = Current;
        if (client is null)
        {
            return null;
        }

        var languageServerRpc = client.rpc;
        if (languageServerRpc is null)
        {
            return null;
        }

        try
        {
            Log($"hover-request-start file={Path.GetFileName(filePath)} line={line} char={character}");
            var uri = new Uri(filePath).AbsoluteUri;
            var response = await languageServerRpc.InvokeWithParameterObjectAsync<JToken?>(
                "textDocument/hover",
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

            var markdown = ExtractHoverText(response);
            if (string.IsNullOrWhiteSpace(markdown))
            {
                Log($"hover-request-empty file={Path.GetFileName(filePath)} line={line} char={character}");
                return null;
            }

            Log($"hover-request-success file={Path.GetFileName(filePath)} line={line} char={character} markdownLength={markdown.Length}");
            return markdown;
        }
        catch (Exception ex)
        {
            Log($"hover-request-failed type={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }

    private static string ResolveWorkspacePath(string extensionDirectory)
    {
        var solutionRoot = TryGetSolutionDirectory();
        if (!string.IsNullOrWhiteSpace(solutionRoot))
        {
            return solutionRoot!;
        }

        var envWorkspace = Environment.GetEnvironmentVariable("QUERYLENS_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(envWorkspace) && Directory.Exists(envWorkspace))
        {
            return Path.GetFullPath(envWorkspace);
        }

        var repoRoot = TryFindRepositoryRoot(extensionDirectory);
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            return repoRoot!;
        }

        return Environment.CurrentDirectory;
    }

    private static string ResolveServerPath(string extensionDirectory, string workspaceRoot)
    {
        var overridePath = Environment.GetEnvironmentVariable(LspDllOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var packagedServerPath = Path.Combine(extensionDirectory, "server", "EFQueryLens.Lsp.dll");
        if (File.Exists(packagedServerPath))
        {
            return packagedServerPath;
        }

        var rootServerPath = Path.Combine(extensionDirectory, "EFQueryLens.Lsp.dll");
        if (File.Exists(rootServerPath))
        {
            return rootServerPath;
        }

        var repoRoot = ResolveRepositoryRoot(workspaceRoot, extensionDirectory);
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var release = Path.Combine(repoRoot, "src", "EFQueryLens.Lsp", "bin", "Release", "net10.0", "EFQueryLens.Lsp.dll");
            if (File.Exists(release))
            {
                return release;
            }

            var debug = Path.Combine(repoRoot, "src", "EFQueryLens.Lsp", "bin", "Debug", "net10.0", "EFQueryLens.Lsp.dll");
            if (File.Exists(debug))
            {
                return debug;
            }

            var published = Path.Combine(repoRoot, "src", "EFQueryLens.Lsp", "bin", "Debug", "net10.0", "publish", "EFQueryLens.Lsp.dll");
            if (File.Exists(published))
            {
                return published;
            }
        }

        return packagedServerPath;
    }

    private static void ConfigureEnvironment(ProcessStartInfo processStartInfo, string workspaceRoot, string serverPath)
    {
        processStartInfo.Environment["QUERYLENS_WORKSPACE"] = workspaceRoot;
        processStartInfo.Environment["QUERYLENS_DAEMON_WORKSPACE"] = workspaceRoot;
        processStartInfo.Environment["QUERYLENS_DAEMON_START_TIMEOUT_MS"] = "30000";
        processStartInfo.Environment["QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS"] = "10000";
        processStartInfo.Environment["QUERYLENS_DAEMON_SHUTDOWN_ON_DISPOSE"] = "1";
        processStartInfo.Environment["QUERYLENS_MAX_CODELENS_PER_DOCUMENT"] = DefaultMaxCodeLensPerDocument.ToString(System.Globalization.CultureInfo.InvariantCulture);
        processStartInfo.Environment["QUERYLENS_CODELENS_DEBOUNCE_MS"] = DefaultCodeLensDebounceMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        processStartInfo.Environment["QUERYLENS_CODELENS_USE_MODEL_FILTER"] = "0";
        processStartInfo.Environment["QUERYLENS_CLIENT"] = "vs";
        processStartInfo.Environment["QUERYLENS_DEBUG"] = "1";
        processStartInfo.Environment["QUERYLENS_ENABLE_LSP_HOVER"] = "0";
        var lspLogPath = BuildLspLogFilePath(workspaceRoot);
        processStartInfo.Environment["QUERYLENS_LSP_LOG_FILE"] = lspLogPath;
        currentLspLogPath = lspLogPath;
        // serverPath is {extRoot}/server/EFQueryLens.Lsp.dll; parent = {extRoot}/server/, grandparent = {extRoot}
        var extensionDirectory = Path.GetDirectoryName(serverPath) ?? string.Empty;
        var extensionRoot = Path.GetDirectoryName(extensionDirectory) ?? extensionDirectory;
        var repoRoot = ResolveRepositoryRoot(workspaceRoot, extensionDirectory);

        // Bundled daemon (from VSIX) takes priority; repo-root paths are the dev-time fallback
        var daemonExeCandidates = new List<string>
        {
            Path.Combine(extensionRoot, "daemon", "EFQueryLens.Daemon.exe"),
        };
        var daemonDllCandidates = new List<string>
        {
            Path.Combine(extensionRoot, "daemon", "EFQueryLens.Daemon.dll"),
        };
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            daemonExeCandidates.Add(Path.Combine(repoRoot, "src", "EFQueryLens.Daemon", "bin", "Debug", "net10.0", "EFQueryLens.Daemon.exe"));
            daemonExeCandidates.Add(Path.Combine(repoRoot, "src", "EFQueryLens.Daemon", "bin", "Release", "net10.0", "EFQueryLens.Daemon.exe"));
            daemonDllCandidates.Add(Path.Combine(repoRoot, "src", "EFQueryLens.Daemon", "bin", "Debug", "net10.0", "EFQueryLens.Daemon.dll"));
            daemonDllCandidates.Add(Path.Combine(repoRoot, "src", "EFQueryLens.Daemon", "bin", "Release", "net10.0", "EFQueryLens.Daemon.dll"));
        }

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

    private static string? ResolveRepositoryRoot(string workspaceRoot, string extensionDirectory)
    {
        var overrideRoot = Environment.GetEnvironmentVariable(RepositoryRootOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var normalized = Path.GetFullPath(overrideRoot);
            if (File.Exists(Path.Combine(normalized, "EFQueryLens.slnx")))
            {
                return normalized;
            }
        }

        if (File.Exists(Path.Combine(workspaceRoot, "EFQueryLens.slnx")))
        {
            return workspaceRoot;
        }

        return TryFindRepositoryRoot(extensionDirectory);
    }

    private static string? TryFindRepositoryRoot(string startDirectory)
    {
        try
        {
            var current = new DirectoryInfo(startDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "EFQueryLens.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }
        catch
        {
            // Best effort only.
        }

        return null;
    }

    private static string BuildLspLogFilePath(string workspaceRoot)
    {
        var normalizedWorkspace = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        byte[] hashBytes;
        using (var sha = SHA256.Create())
        {
            hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedWorkspace));
        }

        var hash = BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
        if (hash.Length > 16)
        {
            hash = hash.Substring(0, 16);
        }

        var directory = Path.Combine(Path.GetTempPath(), "EFQueryLens", "vs-logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"lsp-{hash}.log");
    }

    private static string? TryGetSolutionDirectory()
    {
        try
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
                if (solution is null)
                {
                    return null;
                }

                solution.GetSolutionInfo(out var solutionDirectory, out _, out _);
                if (string.IsNullOrWhiteSpace(solutionDirectory))
                {
                    return null;
                }

                return Path.GetFullPath(solutionDirectory);
            });
        }
        catch
        {
            return null;
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

    private static string ExtractHoverText(JToken? hover)
    {
        var contents = hover?["contents"];
        if (contents is null)
        {
            return string.Empty;
        }

        if (contents.Type == JTokenType.String)
        {
            return contents.Value<string>() ?? string.Empty;
        }

        if (contents.Type == JTokenType.Array)
        {
            var segments = new List<string>();
            foreach (var item in contents)
            {
                var value = item?["value"]?.Value<string>() ?? item?.Value<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    segments.Add(value!);
                }
            }

            return string.Join("\n\n", segments);
        }

        if (contents.Type == JTokenType.Object)
        {
            return contents["value"]?.Value<string>() ?? string.Empty;
        }

        return string.Empty;
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
}
