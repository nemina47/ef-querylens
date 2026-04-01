// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

/// <summary>
/// Listens to text buffer events and sends document sync notifications to the LSP server.
/// This ensures that the LSP server's DocumentManager always contains the latest source text,
/// so local variable type extraction works correctly regardless of file encoding or line endings.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("CSharp")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class DocumentSyncProvider : IWpfTextViewCreationListener
{
    private readonly Dictionary<string, TrackedSyncBuffer> _syncedBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IWpfTextView, string> _viewToFile = new();
    private static readonly object SyncLock = new();

    public void TextViewCreated(IWpfTextView textView)
    {
        if (textView?.TextBuffer is null)
            return;

        var buffer = textView.TextBuffer;
        if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document))
            return;

        string filePath = document.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return;

        textView.Closed += OnTextViewClosed;

        lock (SyncLock)
        {
            _viewToFile[textView] = filePath;

            if (_syncedBuffers.TryGetValue(filePath, out var tracked))
            {
                tracked.RefCount++;
                return;
            }

            DocumentSyncBuffer syncBuffer = new(filePath, buffer);
            _syncedBuffers[filePath] = new TrackedSyncBuffer(syncBuffer, refCount: 1);

            // Send initial didOpen notification with current buffer content
            string sourceText = buffer.CurrentSnapshot.GetText();
            QueryLensLanguageClient.NotifyDocumentOpened(filePath, sourceText);
        }
    }

    private void OnTextViewClosed(object? sender, EventArgs e)
    {
        if (sender is not IWpfTextView view)
            return;

        view.Closed -= OnTextViewClosed;

        lock (SyncLock)
        {
            if (!_viewToFile.TryGetValue(view, out string? filePath))
                return;

            _viewToFile.Remove(view);

            if (!_syncedBuffers.TryGetValue(filePath, out var tracked))
                return;

            tracked.RefCount--;
            if (tracked.RefCount > 0)
                return;

            _syncedBuffers.Remove(filePath);
            tracked.Buffer.Dispose();
        }
    }

    private sealed class TrackedSyncBuffer
    {
        internal TrackedSyncBuffer(DocumentSyncBuffer buffer, int refCount)
        {
            Buffer = buffer;
            RefCount = refCount;
        }

        internal DocumentSyncBuffer Buffer { get; }

        internal int RefCount { get; set; }
    }

    private sealed class DocumentSyncBuffer : IDisposable
    {
        private readonly string _filePath;
        private readonly ITextBuffer _buffer;
        private bool _disposed;

        internal DocumentSyncBuffer(string filePath, ITextBuffer buffer)
        {
            _filePath = filePath;
            _buffer = buffer;

            _buffer.ChangedHighPriority += OnTextBufferChanged;
            _buffer.Properties.AddProperty(typeof(DocumentSyncBuffer), this);
        }

        private void OnTextBufferChanged(object? sender, TextContentChangedEventArgs e)
        {
            if (_disposed)
                return;

            // When the buffer content changes, send didChange notification with full content
            string sourceText = _buffer.CurrentSnapshot.GetText();
            QueryLensLanguageClient.NotifyDocumentChanged(_filePath, sourceText);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _buffer.ChangedHighPriority -= OnTextBufferChanged;
            _buffer.Properties.RemoveProperty(typeof(DocumentSyncBuffer));

            // Send didClose notification to drain LSP server cache
            QueryLensLanguageClient.NotifyDocumentClosed(_filePath);
        }
    }
}
