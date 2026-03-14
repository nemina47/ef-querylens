// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

internal static class QueryLensLogOpener
{
    private static readonly object tailSync = new();
    private static readonly Guid outputPaneGuid = new("B7A8AF5E-4B7A-4D4D-8E42-520A6CB3A4D2");
    private static readonly TimeSpan tailInterval = TimeSpan.FromSeconds(1);

    private static Timer? tailTimer;
    private static IVsOutputWindowPane? outputPane;
    private static string? activeLogPath;
    private static long lastReadPosition;
    private static bool paneInitialized;

    internal static async Task InitializeOutputPaneAsync(AsyncPackage package, CancellationToken cancellationToken)
    {
        var pane = await EnsureOutputPaneAsync(package, cancellationToken);
        if (pane is null)
        {
            return;
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        lock (tailSync)
        {
            if (paneInitialized)
            {
                return;
            }

            paneInitialized = true;
            outputPane = pane;
        }

        pane.OutputString($"EF QueryLens output initialized ({DateTime.UtcNow:O}){Environment.NewLine}");
    }

    internal static async Task<(bool Success, string Message)> StartTailInOutputWindowAsync(AsyncPackage package, CancellationToken cancellationToken)
    {
        var candidates = QueryLensLanguageClient.GetLogFilePaths()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            return (false, "No QueryLens log file path is available yet. Trigger a hover first.");
        }

        var selectedPath = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        EnsureFileExists(selectedPath);

        var pane = await EnsureOutputPaneAsync(package, cancellationToken);
        if (pane is null)
        {
            return (false, "Failed to access Visual Studio Output window pane.");
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        pane.Activate();
        pane.OutputString($"{Environment.NewLine}=== EF QueryLens log tail started ({DateTime.UtcNow:O}) ==={Environment.NewLine}");
        pane.OutputString($"Source: {selectedPath}{Environment.NewLine}");

        var snapshot = ReadLastLines(selectedPath, maxLines: 120);
        if (!string.IsNullOrWhiteSpace(snapshot))
        {
            pane.OutputString($"--- Last 120 lines ---{Environment.NewLine}");
            pane.OutputString(snapshot + Environment.NewLine);
            pane.OutputString("--- Live tail ---" + Environment.NewLine);
        }

        lock (tailSync)
        {
            activeLogPath = selectedPath;
            lastReadPosition = GetFileLength(selectedPath);
            outputPane = pane;

            if (tailTimer is null)
            {
                tailTimer = new Timer(static _ => TailTick(), null, tailInterval, tailInterval);
            }
            else
            {
                tailTimer.Change(tailInterval, tailInterval);
            }
        }

        return (true, selectedPath);
    }

    private static void EnsureFileExists(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(path))
        {
            File.WriteAllText(path, $"EF QueryLens Log{Environment.NewLine}Created (UTC): {DateTime.UtcNow:O}{Environment.NewLine}");
        }
    }

    private static async Task<IVsOutputWindowPane?> EnsureOutputPaneAsync(AsyncPackage package, CancellationToken cancellationToken)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var outputWindow = await package.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
        if (outputWindow is null)
        {
            return null;
        }

        var paneGuid = outputPaneGuid;
        outputWindow.CreatePane(ref paneGuid, "EF QueryLens", 1, 1);
        outputWindow.GetPane(ref paneGuid, out var pane);
        return pane;
    }

    private static void TailTick()
    {
        string? path;
        long position;
        IVsOutputWindowPane? pane;

        lock (tailSync)
        {
            path = activeLogPath;
            position = lastReadPosition;
            pane = outputPane;
        }

        if (string.IsNullOrWhiteSpace(path) || pane is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < position)
            {
                position = 0;
#pragma warning disable VSTHRD010 // OutputStringThreadSafe is safe from background threads.
                pane.OutputStringThreadSafe($"{Environment.NewLine}--- Log rotated/truncated, continuing from beginning ---{Environment.NewLine}");
#pragma warning restore VSTHRD010
            }

            if (stream.Length == position)
            {
                return;
            }

            stream.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var appended = reader.ReadToEnd();
            var nextPosition = stream.Position;

            if (!string.IsNullOrEmpty(appended))
            {
#pragma warning disable VSTHRD010 // OutputStringThreadSafe is safe from background threads.
                pane.OutputStringThreadSafe(appended);
#pragma warning restore VSTHRD010
            }

            lock (tailSync)
            {
                if (string.Equals(activeLogPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    lastReadPosition = nextPosition;
                }
            }
        }
        catch
        {
            // Best effort tailing.
        }
    }

    private static long GetFileLength(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists ? fileInfo.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadLastLines(string path, int maxLines)
    {
        try
        {
            var tail = new Queue<string>(maxLines);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (tail.Count == maxLines)
                {
                    _ = tail.Dequeue();
                }

                tail.Enqueue(line);
            }

            return string.Join(Environment.NewLine, tail);
        }
        catch
        {
            return string.Empty;
        }
    }
}
