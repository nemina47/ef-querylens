// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

internal sealed partial class QueryLensLanguageClient
{
    private static async System.Threading.Tasks.Task<string> ResolveWorkspacePathAsync(string extensionDirectory)
    {
        var solutionDirectory = await TryGetSolutionDirectoryAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return solutionDirectory!;
        }

        string envWorkspace = Environment.GetEnvironmentVariable("QUERYLENS_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(envWorkspace) && Directory.Exists(envWorkspace))
        {
            return Path.GetFullPath(envWorkspace);
        }

        return Environment.CurrentDirectory;
    }

    private static string ResolveServerPath(string extensionDirectory)
    {
        string overridePath = Environment.GetEnvironmentVariable(LspDllOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        string packagedServerPath = Path.Combine(extensionDirectory, "server", "EFQueryLens.Lsp.dll");
        if (File.Exists(packagedServerPath))
        {
            return packagedServerPath;
        }

        string rootServerPath = Path.Combine(extensionDirectory, "EFQueryLens.Lsp.dll");
        if (File.Exists(rootServerPath))
        {
            return rootServerPath;
        }

        return packagedServerPath;
    }

    private static string BuildLspLogFilePath(string workspaceRoot)
    {
        string normalizedWorkspace = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        byte[] hashBytes;
        using (SHA256 sha = SHA256.Create())
        {
            hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedWorkspace));
        }

        string hash = BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
        if (hash.Length > 16)
        {
            hash = hash.Substring(0, 16);
        }

        string directory = Path.Combine(Path.GetTempPath(), "EFQueryLens", "vs-logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"lsp-{hash}.log");
    }

    private static async System.Threading.Tasks.Task<string?> TryGetSolutionDirectoryAsync()
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            IVsSolution? solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
            if (solution is null)
            {
                return null;
            }

            solution.GetSolutionInfo(out string? solutionDirectory, out _, out _);
            if (string.IsNullOrWhiteSpace(solutionDirectory))
            {
                return null;
            }

            return Path.GetFullPath(solutionDirectory);
        }
        catch
        {
            return null;
        }
    }

}
