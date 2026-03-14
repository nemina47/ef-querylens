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
[InstalledProductRegistration("EF QueryLens", "Preview EF Core LINQ SQL in Visual Studio", "0.2.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[Guid(QueryLensCommandGuids.PackageString)]
internal sealed class QueryLensPackage : AsyncPackage
{
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await GetServiceAsync(typeof(IMenuCommandService)) is not OleMenuCommandService menuCommandService)
        {
            return;
        }

        await QueryLensLogOpener.InitializeOutputPaneAsync(this, cancellationToken);

        AddMenuCommand(menuCommandService, QueryLensCommandIds.RestartDaemon, HandleRestartDaemonCommand);
        AddMenuCommand(menuCommandService, QueryLensCommandIds.OpenLogs, HandleOpenLogsCommand);
    }

    private static void AddMenuCommand(OleMenuCommandService menuCommandService, int commandId, EventHandler handler)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var menuCommandId = new CommandID(new Guid(QueryLensCommandGuids.CommandSetString), commandId);
        var menuCommand = new OleMenuCommand(handler, menuCommandId);
        menuCommandService.AddCommand(menuCommand);
    }

    private void HandleRestartDaemonCommand(object sender, EventArgs e)
    {
        _ = JoinableTaskFactory.RunAsync(async delegate
        {
            var result = await QueryLensLanguageClient.RequestDaemonRestartAsync(CancellationToken.None);

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var icon = result.Success ? OLEMSGICON.OLEMSGICON_INFO : OLEMSGICON.OLEMSGICON_WARNING;
            VsShellUtilities.ShowMessageBox(
                this,
                result.Message,
                "EF QueryLens",
                icon,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            });
    }

    private void HandleOpenLogsCommand(object sender, EventArgs e)
    {
        _ = JoinableTaskFactory.RunAsync(async delegate
        {
            var (success, message) = await QueryLensLogOpener.StartTailInOutputWindowAsync(this, CancellationToken.None);

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
}
