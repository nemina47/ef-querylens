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

        int position = triggerPoint.Value.Position;
        var applicableSpan = BuildApplicableSpan(snapshot, position);

        if (!TryMarkSessionContent(session))
        {
            return null;
        }

        SnapshotSpan applicableSnapshotSpan = new(snapshot, applicableSpan);
        ITrackingSpan? applicableTrackingSpan = snapshot.CreateTrackingSpan(applicableSnapshotSpan, SpanTrackingMode.EdgeInclusive);

        // Try structured hover first (VS-optimized path: typed SQL, always-pinned header, no markdown parsing).
        var structuredElement = await TryGetStructuredContentAsync(triggerPoint.Value, snapshot, applicableSpan, cancellationToken).ConfigureAwait(false);
        if (structuredElement is not null)
        {
            return new QuickInfoItem(applicableTrackingSpan, structuredElement);
        }

        string documentUri = "about:blank";
        if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument unavailableDocument)
            && !string.IsNullOrWhiteSpace(unavailableDocument.FilePath))
        {
            documentUri = new Uri(unavailableDocument.FilePath).AbsoluteUri;
        }

        var startupStatus = QueryLensLanguageClient.GetStartupStatus();
        var (statusCode, statusMessage, errorMessage) = startupStatus switch
        {
            LspStartupStatus.Starting =>
                (2, // QueryTranslationStatus.Starting
                 "EF QueryLens is starting up \u2014 hover again in a moment.",
                 "Language server is initializing."),
            LspStartupStatus.NotStarted =>
                (2,
                 "EF QueryLens is loading \u2014 open a C\u266f file and hover again shortly.",
                 "Language server has not activated yet."),
            _ =>
                (3, // QueryTranslationStatus.DaemonUnavailable
                 "EF QueryLens is unavailable. Check that the extension installed correctly and try reloading.",
                 "EF QueryLens is unavailable."),
        };

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var content = LinqHoverMarkdownRenderer.CreateFromStructured(
            new QueryLensStructuredHoverResponse
            {
                Success = false,
                Status = statusCode,
                StatusMessage = statusMessage,
                ErrorMessage = errorMessage,
                CommandCount = 0,
                Statements = [],
                Warnings = [],
            },
            documentUri,
            0,
            0);
        QuickInfoItem item = new(applicableTrackingSpan, content);
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

        string uri = new Uri(document.FilePath).AbsoluteUri;
        var attempts = BuildHoverAttempts(triggerPoint, snapshot, applicableSpan);
        for (int i = 0; i < attempts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = attempts[i];
            Log($"Structured hover attempt {i + 1}/{attempts.Count} ({attempt.Label}) line={attempt.Line} char={attempt.Character}");

            var response = await QueryLensLanguageClient.TryGetStructuredHoverAsync(
                document.FilePath,
                attempt.Line,
                attempt.Character,
                cancellationToken).ConfigureAwait(false);

            if (response is not null)
            {
                Log($"Structured hover resolved on attempt {i + 1} ({attempt.Label}), success={response.Success}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                return LinqHoverMarkdownRenderer.CreateFromStructured(response, uri, attempt.Line, attempt.Character);
            }

            if (i < attempts.Count - 1)
            {
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
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

        int safePosition = Math.Max(0, Math.Min(position, snapshot.Length - 1));
        var line = snapshot.GetLineFromPosition(safePosition);
        string lineText = line.GetText();
        if (lineText.Length == 0)
        {
            return new Span(safePosition, 1);
        }

        int lineOffset = Math.Max(0, Math.Min(safePosition - line.Start.Position, lineText.Length - 1));
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

        int start = lineOffset;
        while (start > 0 && IsIdentifierChar(lineText[start - 1]))
        {
            start--;
        }

        int endExclusive = lineOffset + 1;
        while (endExclusive < lineText.Length && IsIdentifierChar(lineText[endExclusive]))
        {
            endExclusive++;
        }

        int absoluteStart = line.Start.Position + start;
        int length = Math.Max(1, endExclusive - start);
        return new Span(absoluteStart, length);
    }

    private static bool IsIdentifierChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch == '_';
    }

    private static System.Collections.Generic.List<HoverAttempt> BuildHoverAttempts(SnapshotPoint triggerPoint, ITextSnapshot snapshot, Span applicableSpan)
    {
        List<HoverAttempt> attempts = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        static void AddAttempt(
            System.Collections.Generic.List<HoverAttempt> list,
            System.Collections.Generic.HashSet<string> dedupe,
            string label,
            int line,
            int character)
        {
            string key = $"{line}:{character}";
            if (!dedupe.Add(key))
            {
                return;
            }

            list.Add(new HoverAttempt(label, line, character));
        }

        var triggerLine = triggerPoint.GetContainingLine();
        int triggerChar = Math.Max(0, triggerPoint.Position - triggerLine.Start.Position);
        AddAttempt(attempts, seen, "trigger", triggerLine.LineNumber, triggerChar);
        AddAttemptFromAbsolute(snapshot, attempts, seen, "trigger-prev", Math.Max(0, triggerPoint.Position - 1));
        AddAttemptFromAbsolute(snapshot, attempts, seen, "trigger-next", Math.Min(snapshot.Length - 1, triggerPoint.Position + 1));

        if (snapshot.Length > 0)
        {
            int startPos = Math.Max(0, Math.Min(applicableSpan.Start, snapshot.Length - 1));
            int endPos = Math.Max(0, Math.Min(Math.Max(applicableSpan.End - 1, applicableSpan.Start), snapshot.Length - 1));
            int midPos = Math.Max(0, Math.Min(applicableSpan.Start + (applicableSpan.Length / 2), snapshot.Length - 1));

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

        int pos = Math.Max(0, Math.Min(absolutePosition, snapshot.Length - 1));
        SnapshotPoint point = new(snapshot, pos);
        var line = point.GetContainingLine();
        int character = Math.Max(0, point.Position - line.Start.Position);

        string key = $"{line.LineNumber}:{character}";
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
            string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch
        {
            // Ignore logging failures
        }
    }
}

