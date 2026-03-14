// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System.Windows.Forms;
using Shared;
using Microsoft.VisualStudio.Shell;

internal static class LinqHoverDocumentationPopup
{
    public static void Show()
    {
        if (ThreadHelper.CheckAccess())
        {
            ShowOnMainThread();
            return;
        }

        ThreadHelper.JoinableTaskFactory.Run(async delegate
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ShowOnMainThread();
        });
    }

    private static void ShowOnMainThread()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        using var form = new Form();
        form.Text = "LINQ Hover Documentation";
        form.Width = 760;
        form.Height = 560;
        form.StartPosition = FormStartPosition.CenterScreen;

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("Consolas", 10f),
            Text = EfQueryLensHoverMarkdownContent.DocumentationMarkdown,
        };

        form.Controls.Add(textBox);
        form.ShowDialog();
    }
}

