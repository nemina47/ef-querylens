// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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
        if (snapshot.Length == 0)
        {
            return null;
        }

        var position = triggerPoint.Value.Position;
        var applicableSpan = BuildApplicableSpan(snapshot, position);

        if (!TryMarkSessionContent(session))
        {
            return null;
        }

        var applicableSnapshotSpan = new SnapshotSpan(snapshot, applicableSpan);
        ITrackingSpan? applicableTrackingSpan = snapshot.CreateTrackingSpan(applicableSnapshotSpan, SpanTrackingMode.EdgeInclusive);

        // Try structured hover first (VS-optimized path: typed SQL, always-pinned header, no markdown parsing).
        var structuredElement = await TryGetStructuredContentAsync(triggerPoint.Value, snapshot, applicableSpan, cancellationToken);
        if (structuredElement is not null)
        {
            return new QuickInfoItem(applicableTrackingSpan, structuredElement);
        }

        var documentUri = "about:blank";
        if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument unavailableDocument)
            && !string.IsNullOrWhiteSpace(unavailableDocument.FilePath))
        {
            documentUri = new Uri(unavailableDocument.FilePath).AbsoluteUri;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var content = LinqHoverMarkdownRenderer.CreateFromStructured(
            new QueryLensStructuredHoverResponse
            {
                Success = false,
                Status = 3,
                StatusMessage = "EF QueryLens structured hover response is unavailable. Ensure daemon/LSP are running and retry.",
                ErrorMessage = "EF QueryLens structured hover response is unavailable.",
                CommandCount = 0,
                Statements = [],
                Warnings = [],
            },
            documentUri,
            0,
            0);
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

    private async Task<System.Windows.FrameworkElement?> TryGetStructuredContentAsync(SnapshotPoint triggerPoint, ITextSnapshot snapshot, Span applicableSpan, CancellationToken cancellationToken)
    {
        if (!textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
            || string.IsNullOrWhiteSpace(document.FilePath))
        {
            return null;
        }

        var uri = new Uri(document.FilePath).AbsoluteUri;
        var attempts = BuildHoverAttempts(triggerPoint, snapshot, applicableSpan);
        for (var i = 0; i < attempts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = attempts[i];
            Log($"Structured hover attempt {i + 1}/{attempts.Count} ({attempt.Label}) line={attempt.Line} char={attempt.Character}");

            var response = await QueryLensLanguageClient.TryGetStructuredHoverAsync(
                document.FilePath,
                attempt.Line,
                attempt.Character,
                cancellationToken);

            if (response is not null)
            {
                Log($"Structured hover resolved on attempt {i + 1} ({attempt.Label}), success={response.Success}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                return LinqHoverMarkdownRenderer.CreateFromStructured(response, uri, attempt.Line, attempt.Character);
            }

            if (i < attempts.Count - 1)
            {
                await Task.Delay(120, cancellationToken);
            }
        }

        Log("Structured hover returned null for all attempts.");
        return null;
    }

    private static Span BuildApplicableSpan(ITextSnapshot snapshot, int position)
    {
        if (snapshot.Length == 0)
        {
            return new Span(0, 0);
        }

        var safePosition = Math.Max(0, Math.Min(position, snapshot.Length - 1));
        var line = snapshot.GetLineFromPosition(safePosition);
        var lineText = line.GetText();
        if (lineText.Length == 0)
        {
            return new Span(safePosition, 1);
        }

        var lineOffset = Math.Max(0, Math.Min(safePosition - line.Start.Position, lineText.Length - 1));
        if (!IsIdentifierChar(lineText[lineOffset])
            && lineOffset > 0
            && IsIdentifierChar(lineText[lineOffset - 1]))
        {
            lineOffset--;
        }

        if (!IsIdentifierChar(lineText[lineOffset]))
        {
            return new Span(safePosition, 1);
        }

        var start = lineOffset;
        while (start > 0 && IsIdentifierChar(lineText[start - 1]))
        {
            start--;
        }

        var endExclusive = lineOffset + 1;
        while (endExclusive < lineText.Length && IsIdentifierChar(lineText[endExclusive]))
        {
            endExclusive++;
        }

        var absoluteStart = line.Start.Position + start;
        var length = Math.Max(1, endExclusive - start);
        return new Span(absoluteStart, length);
    }

    private static bool IsIdentifierChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch == '_';
    }

    private static System.Collections.Generic.List<HoverAttempt> BuildHoverAttempts(SnapshotPoint triggerPoint, ITextSnapshot snapshot, Span applicableSpan)
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
        AddAttemptFromAbsolute(snapshot, attempts, seen, "trigger-prev", Math.Max(0, triggerPoint.Position - 1));
        AddAttemptFromAbsolute(snapshot, attempts, seen, "trigger-next", Math.Min(snapshot.Length - 1, triggerPoint.Position + 1));

        if (snapshot.Length > 0)
        {
            var startPos = Math.Max(0, Math.Min(applicableSpan.Start, snapshot.Length - 1));
            var endPos = Math.Max(0, Math.Min(Math.Max(applicableSpan.End - 1, applicableSpan.Start), snapshot.Length - 1));
            var midPos = Math.Max(0, Math.Min(applicableSpan.Start + (applicableSpan.Length / 2), snapshot.Length - 1));

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

