// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("EF QueryLens", "Preview EF Core LINQ SQL in Visual Studio", "0.0.1")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[Guid(QueryLensCommandGuids.PackageString)]
internal sealed class QueryLensPackage : AsyncPackage
{
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await GetServiceAsync(typeof(IMenuCommandService)).ConfigureAwait(false) is not OleMenuCommandService menuCommandService)
        {
            return;
        }

        await QueryLensLogOpener.InitializeOutputPaneAsync(this, cancellationToken).ConfigureAwait(false);

        AddMenuCommand(menuCommandService, QueryLensCommandIds.RestartDaemon, HandleRestartDaemonCommand);
        AddMenuCommand(menuCommandService, QueryLensCommandIds.OpenLogs, HandleOpenLogsCommand);
    }

    private static void AddMenuCommand(OleMenuCommandService menuCommandService, int commandId, EventHandler handler)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        CommandID menuCommandId = new(new Guid(QueryLensCommandGuids.CommandSetString), commandId);
        OleMenuCommand menuCommand = new(handler, menuCommandId);
        menuCommandService.AddCommand(menuCommand);
    }

    private void HandleRestartDaemonCommand(object sender, EventArgs e)
    {
        RunCommand(async cancellationToken =>
        {
            var result = await QueryLensLanguageClient.RequestDaemonRestartAsync(cancellationToken).ConfigureAwait(false);

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var icon = result.Success ? OLEMSGICON.OLEMSGICON_INFO : OLEMSGICON.OLEMSGICON_WARNING;
            VsShellUtilities.ShowMessageBox(
                this,
                result.Success ? result.Message : $"[{result.Code}] {result.Message}",
                "EF QueryLens",
                icon,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        });
    }

    private void HandleOpenLogsCommand(object sender, EventArgs e)
    {
        RunCommand(async cancellationToken =>
        {
            var (success, message) = await QueryLensLogOpener.StartTailInOutputWindowAsync(this, cancellationToken).ConfigureAwait(false);

            if (success)
            {
                return;
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(
                this,
                message,
                "EF QueryLens",
                OLEMSGICON.OLEMSGICON_WARNING,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            QueryLensLogOpener.StopTail();
            QueryLensLanguageClient.DisposeCurrent();
        }

        base.Dispose(disposing);
    }

    private void RunCommand(Func<CancellationToken, Task> action)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var commandTask = JoinableTaskFactory.RunAsync(async delegate
        {
            try
            {
                await action(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(
                    this,
                    $"[{QueryLensErrorCodes.CommandExecutionFailed}] EF QueryLens command failed: {ex.Message}",
                    "EF QueryLens",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        });

        commandTask.FileAndForget("efquerylens/QueryLensPackage/RunCommand");
    }
}
