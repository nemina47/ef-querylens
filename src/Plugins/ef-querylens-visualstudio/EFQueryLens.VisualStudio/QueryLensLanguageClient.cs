// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using StreamJsonRpc;

[Export(typeof(ILanguageClient))]
[ContentType("CSharp")]
[Name("EF QueryLens")]
internal sealed partial class QueryLensLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
{
    private const int DefaultMaxCodeLensPerDocument = 50;
    private const int DefaultCodeLensDebounceMilliseconds = 250;
    private const string LspDllOverrideEnvVar = "QUERYLENS_LSP_DLL";
    private static readonly object Sync = new();
    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "EFQueryLens.VisualStudio.log");
    private static string? currentLspLogPath;

    private Process? serverProcess;
    private Task? serverErrorPumpTask;
    private CancellationTokenSource? serverErrorPumpCts;
    private JsonRpc? rpc;
    private int disposeRequested;

    internal static QueryLensLanguageClient? Current { get; private set; }

    /// <summary>
    /// Returns the current startup state of the language server, used to show
    /// contextual messages in hover popups before the LSP is fully ready.
    /// </summary>
    internal static LspStartupStatus GetStartupStatus()
    {
        var client = Current;
        if (client is null)
            return LspStartupStatus.NotStarted;

        Process? process;
        JsonRpc? rpcChannel;
        lock (Sync)
        {
            process = client.serverProcess;
            rpcChannel = client.rpc;
        }

        if (rpcChannel != null)
            return LspStartupStatus.Ready;

        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                    return LspStartupStatus.Starting;
            }
            catch
            {
                return LspStartupStatus.Starting;
            }
        }

        // Client constructed but ActivateAsync not yet called, or process exited without RPC.
        return LspStartupStatus.NotStarted;
    }

    public QueryLensLanguageClient()
    {
        lock (Sync)
        {
            Current = this;
        }
    }

    public string Name => "EF QueryLens";

    public IEnumerable<string> ConfigurationSections => [];

    public object? InitializationOptions => BuildInitializationOptions();

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
        if (Volatile.Read(ref disposeRequested) == 1)
        {
            throw new ObjectDisposedException(nameof(QueryLensLanguageClient));
        }

        string extensionDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Unable to resolve extension assembly directory.");

        var workspaceRoot = await ResolveWorkspacePathAsync(extensionDirectory).ConfigureAwait(false);
        string serverPath = ResolveServerPath(extensionDirectory);
        Log($"lsp-server-path-resolved extensionDir={extensionDirectory} workspaceRoot={workspaceRoot} serverPath={serverPath} exists={File.Exists(serverPath)}");
        if (!File.Exists(serverPath))
        {
            Log($"lsp-server-not-found path={serverPath}");
            throw new FileNotFoundException("Could not find the QueryLens language server assembly.", serverPath);
        }

        ProcessStartInfo processStartInfo = new()
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
        CancellationTokenSource pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (Sync)
        {
            serverErrorPumpCts = pumpCts;
            serverErrorPumpTask = PumpServerErrorStreamAsync(serverProcess, pumpCts.Token);
        }

        await Task.Yield();
        return new Connection(serverProcess.StandardOutput.BaseStream, serverProcess.StandardInput.BaseStream);
    }

    public void Dispose()
    {
        DisposeCore("dispose");
        GC.SuppressFinalize(this);
    }

    internal static void DisposeCurrent()
    {
        QueryLensLanguageClient? current;
        lock (Sync)
        {
            current = Current;
            Current = null;
        }

        current?.Dispose();
    }

    public async Task OnLoadedAsync()
    {
        Log("language-client-loaded");
        await (StartAsync?.InvokeAsync(this, EventArgs.Empty) ?? Task.CompletedTask).ConfigureAwait(false);
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
}

internal enum LspStartupStatus
{
    /// <summary>The language client has not been created or activated yet.</summary>
    NotStarted,
    /// <summary>The LSP process has started but the JSON-RPC channel is not yet attached.</summary>
    Starting,
    /// <summary>The JSON-RPC channel is attached and the LSP is ready to receive requests.</summary>
    Ready,
}

internal sealed class QueryLensSqlStatementDto
{
    public string? Sql { get; set; }
    public string? SplitLabel { get; set; }
}

internal sealed class QueryLensParameterDto
{
    public string? Name { get; set; }
    public string? ClrType { get; set; }
    public string? InferredValue { get; set; }
}

internal sealed class QueryLensStructuredHoverResponse
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<QueryLensSqlStatementDto>? Statements { get; set; }
    public int CommandCount { get; set; }
    public string? SourceExpression { get; set; }
    public string? ExecutedExpression { get; set; }
    public List<QueryLensParameterDto>? Parameters { get; set; }
    public string? DbContextType { get; set; }
    public string? ProviderName { get; set; }
    public string? SourceFile { get; set; }
    public int SourceLine { get; set; }
    public List<string>? Warnings { get; set; }
    public string? EnrichedSql { get; set; }
    public string? Mode { get; set; }
    public int Status { get; set; }
    public string? StatusMessage { get; set; }
    public double AvgTranslationMs { get; set; }
    public double LastTranslationMs { get; set; }
}
