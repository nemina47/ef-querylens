using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;

namespace EFQueryLens.VisualStudio;

public sealed class EFQueryLensLanguageClient : ILanguageClient
{
    public string Name => "EF QueryLens Language Client";

    public IEnumerable<string> ConfigurationSections => Array.Empty<string>();

    public object InitializationOptions => new { };

    public IEnumerable<string> FilesToWatch => Array.Empty<string>();

    public event AsyncEventHandler<EventArgs>? StartAsync;

    public event AsyncEventHandler<EventArgs>? StopAsync;

    public Task<Connection?> ActivateAsync(CancellationToken token)
    {
        // Scaffold placeholder: wire process start and stdio pipe connection in implementation phase.
        _ = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "EFQueryLens.Lsp.dll",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return Task.FromResult<Connection?>(null);
    }

    public Task OnLoadedAsync() => Task.CompletedTask;

    public Task OnServerInitializedAsync() => Task.CompletedTask;

    public Task OnServerInitializeFailedAsync(Exception e) => Task.CompletedTask;
}
