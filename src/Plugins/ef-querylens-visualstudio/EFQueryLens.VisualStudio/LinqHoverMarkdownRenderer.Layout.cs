// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

internal static partial class LinqHoverMarkdownRenderer
{
    private static FrameworkElement RenderHeading(string text, double size, string? preferredCopySql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        TextBlock tb = new()
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = size,
            Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0)),
            Margin = new Thickness(0, 6, 0, 2),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI"),
        };

        AppendInlineMarkdown(tb.Inlines, text, preferredCopySql);
        return tb;
    }

    private static FrameworkElement RenderParagraph(string text, string? preferredCopySql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        TextBlock tb = new()
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4)),
            Margin = new Thickness(0, 1, 0, 1),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI"),
        };

        AppendInlineMarkdown(tb.Inlines, text, preferredCopySql);
        return tb;
    }

    private static FrameworkElement RenderSecondaryItalic(string text, string? preferredCopySql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        TextBlock tb = new()
        {
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(Color.FromRgb(0xb0, 0xb0, 0xb0)),
            Margin = new Thickness(0, 2, 0, 1),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI"),
        };

        AppendInlineMarkdown(tb.Inlines, text, preferredCopySql);
        return tb;
    }

    private static FrameworkElement RenderHeaderLine(string text, string? preferredCopySql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        TextBlock tb = new()
        {
            FontSize = 13,
            FontWeight = FontWeights.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(0xea, 0xea, 0xea)),
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI"),
        };

        AppendInlineMarkdown(tb.Inlines, text, preferredCopySql);
        return tb;
    }

    private static FrameworkElement RenderBullet(string text, string? preferredCopySql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        DockPanel panel = new() { Margin = new Thickness(0, 1, 0, 1) };
        TextBlock bullet = new()
        {
            Text = "• ",
            Width = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4)),
            FontFamily = new FontFamily("Segoe UI"),
        };
        TextBlock content = new()
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4)),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI"),
        };
        AppendInlineMarkdown(content.Inlines, text, preferredCopySql);
        DockPanel.SetDock(bullet, Dock.Left);
        panel.Children.Add(bullet);
        panel.Children.Add(content);
        return panel;
    }

    private static FrameworkElement RenderCodeBlock(string language, IReadOnlyCollection<string> lines)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        StackPanel codeStack = new() { Margin = new Thickness(0) };

        string? languageLabel = string.IsNullOrWhiteSpace(language)
            ? null
            : language.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(languageLabel))
        {
            codeStack.Children.Add(new TextBlock
            {
                Text = languageLabel,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8a, 0xc9, 0xff)),
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        foreach (string? line in lines.DefaultIfEmpty(string.Empty))
        {
            string displayLine = line.Replace("\\`", "`");
            TextBlock tb = new()
            {
                Text = displayLine,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 0),
                TextWrapping = TextWrapping.NoWrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4)),
            };

            if (string.Equals(language, "diff", StringComparison.OrdinalIgnoreCase))
            {
                if (displayLine.StartsWith("+", StringComparison.Ordinal))
                {
                    tb.Foreground = new SolidColorBrush(Color.FromRgb(0x9c, 0xdc, 0xfe));
                    tb.Background = new SolidColorBrush(Color.FromRgb(0x1b, 0x3b, 0x29));
                }
                else if (displayLine.StartsWith("-", StringComparison.Ordinal))
                {
                    tb.Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0xa1, 0x98));
                    tb.Background = new SolidColorBrush(Color.FromRgb(0x4a, 0x20, 0x23));
                }
            }

            codeStack.Children.Add(tb);
        }

        ScrollViewer innerScrollViewer = new()
        {
            Content = codeStack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        innerScrollViewer.PreviewMouseWheel += ForwardMouseWheelToOuterScrollViewer;

        Border border = new()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 5, 0, 7),
            Child = innerScrollViewer,
        };

        return border;
    }

    private static void ForwardMouseWheelToOuterScrollViewer(object sender, MouseWheelEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (e.Handled || sender is not DependencyObject dependencyObject)
        {
            return;
        }

        var ancestor = FindParentScrollViewer(dependencyObject);
        if (ancestor is null)
        {
            return;
        }

        e.Handled = true;
        MouseWheelEventArgs forwarded = new(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        };
        ancestor.RaiseEvent(forwarded);
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject start)
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
