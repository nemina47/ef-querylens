// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis.Text;

namespace EFQueryLens.VisualStudio;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;

internal sealed class LinqHoverQuickInfoSource(ITextBuffer textBuffer) : IAsyncQuickInfoSource
{
    private static readonly string logPath = Path.Combine(Path.GetTempPath(), "EFQueryLens.VisualStudio.log");
    private static readonly object sessionContentMarker = new();

    public void Dispose()
    {
    }

    public async Task<QuickInfoItem?> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SnapshotPoint? triggerPoint = session.GetTriggerPoint(textBuffer.CurrentSnapshot);
        if (!triggerPoint.HasValue)
        {
            return null;
        }

        ITextSnapshot? snapshot = triggerPoint.Value.Snapshot;
        var position = triggerPoint.Value.Position;
        var sourceText = snapshot.GetText();

        (string? LinqCode, TextSpan? Span, string? ErrorMessage) result = TryExtractLinqAtOrNearPosition(sourceText, position);
        if (result.LinqCode is null || !result.Span.HasValue)
        {
            return null;
        }

        TextSpan linqSpan = result.Span.Value;
        if (linqSpan.Length <= 0 || linqSpan.Start < 0 || linqSpan.End > snapshot.Length)
        {
            Log("Linq hover span validation failed.");
            return null;
        }

        if (!TryMarkSessionContent(session))
        {
            return null;
        }

        var applicableSnapshotSpan = new SnapshotSpan(snapshot, new Span(linqSpan.Start, linqSpan.Length));
        ITrackingSpan? applicableTrackingSpan = snapshot.CreateTrackingSpan(applicableSnapshotSpan, SpanTrackingMode.EdgeInclusive);

        var hoverMarkdown = await TryGetHoverMarkdownAsync(triggerPoint.Value, snapshot, linqSpan, cancellationToken);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var content = BuildQuickInfoContent(hoverMarkdown);
        var item = new QuickInfoItem(applicableTrackingSpan, content);
        return item;
    }

    private static bool TryMarkSessionContent(IAsyncQuickInfoSession session)
    {
        lock (session.Properties)
        {
            if (session.Properties.ContainsProperty(sessionContentMarker))
            {
                return false;
            }

            session.Properties.AddProperty(sessionContentMarker, true);
            return true;
        }
    }

    private static (string? LinqCode, TextSpan? Span, string? ErrorMessage) TryExtractLinqAtOrNearPosition(string sourceText, int position)
    {
        (string? LinqCode, TextSpan? Span, string? ErrorMessage) result = LinqChainExtractorInProc.TryExtractLinqAtPositionWithSpan(sourceText, position);
        if (result.LinqCode is not null)
        {
            return result;
        }

        if (position > 0)
        {
            result = LinqChainExtractorInProc.TryExtractLinqAtPositionWithSpan(sourceText, position - 1);
            if (result.LinqCode is not null)
            {
                return result;
            }
        }

        if (position + 1 >= sourceText.Length)
        {
            return result;
        }

        result = LinqChainExtractorInProc.TryExtractLinqAtPositionWithSpan(sourceText, position + 1);
        return result;
    }

    private async Task<string> TryGetHoverMarkdownAsync(SnapshotPoint triggerPoint, ITextSnapshot snapshot, TextSpan linqSpan, CancellationToken cancellationToken)
    {
        if (!textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
            || string.IsNullOrWhiteSpace(document.FilePath))
        {
            return BuildErrorMarkdown("Could not resolve current document path for QueryLens hover.");
        }

        var attempts = BuildHoverAttempts(triggerPoint, snapshot, linqSpan);
        for (var i = 0; i < attempts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = attempts[i];
            Log($"Hover attempt {i + 1}/{attempts.Count} ({attempt.Label}) line={attempt.Line} char={attempt.Character}");

            var markdown = await QueryLensLanguageClient.TryGetHoverMarkdownAsync(
                document.FilePath,
                attempt.Line,
                attempt.Character,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(markdown))
            {
                Log($"Hover resolved on attempt {i + 1} ({attempt.Label}), markdownLength={markdown!.Length}");
                return markdown!;
            }

            if (i < attempts.Count - 1)
            {
                await Task.Delay(120, cancellationToken);
            }
        }

        Log("Hover returned empty markdown for all attempts.");
        return BuildErrorMarkdown("QueryLens hover response is not ready yet. Try hovering again in a moment.");
    }

    private static System.Collections.Generic.List<HoverAttempt> BuildHoverAttempts(SnapshotPoint triggerPoint, ITextSnapshot snapshot, TextSpan linqSpan)
    {
        var attempts = new System.Collections.Generic.List<HoverAttempt>();
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        static void AddAttempt(
            System.Collections.Generic.List<HoverAttempt> list,
            System.Collections.Generic.HashSet<string> dedupe,
            string label,
            int line,
            int character)
        {
            var key = $"{line}:{character}";
            if (!dedupe.Add(key))
            {
                return;
            }

            list.Add(new HoverAttempt(label, line, character));
        }

        var triggerLine = triggerPoint.GetContainingLine();
        var triggerChar = Math.Max(0, triggerPoint.Position - triggerLine.Start.Position);
        AddAttempt(attempts, seen, "trigger", triggerLine.LineNumber, triggerChar);

        if (snapshot.Length > 0)
        {
            var startPos = Math.Max(0, Math.Min(linqSpan.Start, snapshot.Length - 1));
            var endPos = Math.Max(0, Math.Min(Math.Max(linqSpan.End - 1, linqSpan.Start), snapshot.Length - 1));
            var midPos = Math.Max(0, Math.Min(linqSpan.Start + (linqSpan.Length / 2), snapshot.Length - 1));

            AddAttemptFromAbsolute(snapshot, attempts, seen, "span-start", startPos);
            AddAttemptFromAbsolute(snapshot, attempts, seen, "span-mid", midPos);
            AddAttemptFromAbsolute(snapshot, attempts, seen, "span-end", endPos);
        }

        return attempts;
    }

    private static void AddAttemptFromAbsolute(
        ITextSnapshot snapshot,
        System.Collections.Generic.List<HoverAttempt> attempts,
        System.Collections.Generic.HashSet<string> dedupe,
        string label,
        int absolutePosition)
    {
        if (snapshot.Length == 0)
        {
            return;
        }

        var pos = Math.Max(0, Math.Min(absolutePosition, snapshot.Length - 1));
        var point = new SnapshotPoint(snapshot, pos);
        var line = point.GetContainingLine();
        var character = Math.Max(0, point.Position - line.Start.Position);

        var key = $"{line.LineNumber}:{character}";
        if (!dedupe.Add(key))
        {
            return;
        }

        attempts.Add(new HoverAttempt(label, line.LineNumber, character));
    }

    private readonly struct HoverAttempt(string label, int line, int character)
    {
        public string Label { get; } = label;

        public int Line { get; } = line;

        public int Character { get; } = character;
    }

    private static object BuildQuickInfoContent(string markdown)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return LinqHoverMarkdownRenderer.CreateFromMarkdown(markdown);
    }

    private static string BuildErrorMarkdown(string message)
    {
        return $"**QueryLens Error**\n```text\n{message}\n```";
    }

    private static void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch
        {
            // Ignore logging failures
        }
    }
}

