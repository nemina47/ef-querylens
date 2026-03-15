// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

internal sealed class QueryLensHoverMetadata
{
    [JsonPropertyName("SourceExpression")] public string? SourceExpression { get; set; }
    [JsonPropertyName("ExecutedExpression")] public string? ExecutedExpression { get; set; }
    [JsonPropertyName("Mode")] public string? Mode { get; set; }
    [JsonPropertyName("Warnings")] public IReadOnlyList<string>? Warnings { get; set; }
    [JsonPropertyName("SourceFile")] public string? SourceFile { get; set; }
    [JsonPropertyName("SourceLine")] public int SourceLine { get; set; }
    [JsonPropertyName("DbContextType")] public string? DbContextType { get; set; }
    [JsonPropertyName("ProviderName")] public string? ProviderName { get; set; }
    [JsonPropertyName("CreationStrategy")] public string? CreationStrategy { get; set; }
}

internal static class LinqHoverMarkdownRenderer
{
    private static readonly string logPath = Path.Combine(Path.GetTempPath(), "EFQueryLens.VisualStudio.log");
    private static readonly HashSet<string> sqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select",
        "from",
        "where",
        "and",
        "or",
        "not",
        "join",
        "inner",
        "left",
        "right",
        "outer",
        "on",
        "group",
        "by",
        "order",
        "having",
        "limit",
        "offset",
        "as",
        "in",
        "is",
        "null",
        "count",
        "distinct",
        "exists",
    };

    public static FrameworkElement Create(string linqCode)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return CreateFromMarkdown(BuildMarkdown(linqCode));
    }

    public static FrameworkElement CreateFromMarkdown(string markdown)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var effectiveMarkdown = string.IsNullOrWhiteSpace(markdown)
            ? "**QueryLens Error**\n```text\nNo hover content was returned by QueryLens.\n```"
            : markdown;

        var previewMarkdown = StripQueryLensMetadataComment(effectiveMarkdown);
        var preferredCopySql = TryExtractFirstCodeBlock(effectiveMarkdown, "sql");
        var metadata = TryExtractQueryLensMetadata(effectiveMarkdown);
        var enrichedSql = BuildEnrichedSqlContent(preferredCopySql, metadata);

        var hostBorder = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0),
            MinWidth = 380,
            MaxHeight = 420,
            MaxWidth = 860,
        };

        var (extractedHeaderLine, bodyMarkdown) = ExtractPinnedHeaderAndBody(previewMarkdown);
        var fallbackHeaderLine = BuildFallbackHeaderLine(enrichedSql ?? preferredCopySql);
        var headerLine = extractedHeaderLine ?? fallbackHeaderLine;
        var headerFromFallback = extractedHeaderLine is null;
        Log($"hover-render headerDetected={!headerFromFallback} fallbackUsed={headerFromFallback} header='{TruncateForLog(headerLine, 160)}'");

        var layoutGrid = new Grid();
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerElement = RenderHeaderLine(headerLine, enrichedSql ?? preferredCopySql);
        Grid.SetRow(headerElement, 0);
        layoutGrid.Children.Add(headerElement);

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var stack = new StackPanel();
        foreach (FrameworkElement? element in ParseMarkdown(bodyMarkdown, enrichedSql, suppressHeaderLines: true))
        {
            stack.Children.Add(element);
        }

        scrollViewer.Content = stack;

        Grid.SetRow(scrollViewer, 1);
        layoutGrid.Children.Add(scrollViewer);
        hostBorder.Child = layoutGrid;

        return hostBorder;
    }

    public static FrameworkElement CreateFromStructured(QueryLensStructuredHoverResponse response, string uri, int line, int character)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var status = response.Status;
        var isQueueStatus = status is 1 or 2;
        var isServiceUnavailable = status is 3;

        if (!response.Success && !isQueueStatus && !isServiceUnavailable)
        {
            var errorMessage = response.ErrorMessage ?? "Translation failed.";
            return CreateFromMarkdown($"**QueryLens Error**\n```text\n{errorMessage}\n```");
        }

        var statements = response.Statements ?? [];
        var firstSql = statements.Count > 0 ? statements[0].Sql : null;
        var enrichedSql = string.IsNullOrWhiteSpace(response.EnrichedSql)
            ? BuildEnrichedSqlContentFromResponse(firstSql, response)
            : response.EnrichedSql;
        var copySql = enrichedSql ?? firstSql;

        var queryParams = $"uri={Uri.EscapeDataString(uri)}&line={line}&character={character}";
        var statementWord = response.CommandCount == 1 ? "query" : "queries";
        var statusLabel = BuildStructuredStatusLabel(status, response.AvgTranslationMs);
        var headerText = string.IsNullOrWhiteSpace(copySql)
            ? $"**QueryLens · {response.CommandCount} {statementWord} · {statusLabel}**"
            : $"**QueryLens · {response.CommandCount} {statementWord} · {statusLabel}** | [Copy SQL](efquerylens://copySql?{queryParams}) | [Open SQL Editor](efquerylens://openSqlEditor?{queryParams})";

        var hostBorder = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0),
            MinWidth = 380,
            MaxHeight = 420,
            MaxWidth = 860,
        };

        var layoutGrid = new Grid();
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerElement = RenderHeaderLine(headerText, copySql);
        Grid.SetRow(headerElement, 0);
        layoutGrid.Children.Add(headerElement);

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var stack = new StackPanel();

        if (!response.Success && (!string.IsNullOrWhiteSpace(response.StatusMessage) || !string.IsNullOrWhiteSpace(response.ErrorMessage)))
        {
            var statusMessage = response.StatusMessage ?? response.ErrorMessage ?? "EF QueryLens is processing this query.";
            stack.Children.Add(RenderParagraph(statusMessage, copySql));
        }

        foreach (var stmt in statements)
        {
            if (!string.IsNullOrWhiteSpace(stmt.SplitLabel))
            {
                stack.Children.Add(RenderParagraph($"*{stmt.SplitLabel}*", copySql));
            }
            var sqlLines = (stmt.Sql ?? string.Empty).Replace("\r\n", "\n").Split('\n').ToList();
            stack.Children.Add(RenderCodeBlock("sql", sqlLines));
        }

        var warnings = response.Warnings ?? [];
        if (warnings.Count > 0)
        {
            stack.Children.Add(RenderHeading("Notes", 13, copySql));
            foreach (var w in warnings)
            {
                stack.Children.Add(RenderBullet(w, copySql));
            }
        }

        scrollViewer.Content = stack;
        Grid.SetRow(scrollViewer, 1);
        layoutGrid.Children.Add(scrollViewer);
        hostBorder.Child = layoutGrid;

        return hostBorder;
    }

    private static string BuildStructuredStatusLabel(int status, double avgTranslationMs)
    {
        return status switch
        {
            0 => "🟢 ready",
            1 => avgTranslationMs > 0
                ? $"🔵 queued ({avgTranslationMs:0} ms avg)"
                : "🔵 queued",
            2 => avgTranslationMs > 0
                ? $"🟠 starting ({avgTranslationMs:0} ms avg)"
                : "🟠 starting",
            3 => "🔴 unavailable",
            _ => "ready",
        };
    }

    private static string? BuildEnrichedSqlContentFromResponse(string? rawSql, QueryLensStructuredHoverResponse response)
    {
        if (string.IsNullOrWhiteSpace(rawSql)) return null;

        var sb = new StringBuilder();
        sb.AppendLine("-- EF QueryLens");

        if (!string.IsNullOrWhiteSpace(response.SourceFile))
        {
            var lineDisplay = response.SourceLine > 0 ? $", line {response.SourceLine}" : string.Empty;
            sb.AppendLine($"-- Source:    {response.SourceFile}{lineDisplay}");
        }

        AppendCommentedExpression(sb, "LINQ", response.SourceExpression);

        if (!string.IsNullOrWhiteSpace(response.DbContextType))
        {
            sb.AppendLine($"-- DbContext: {response.DbContextType}");
        }

        if (!string.IsNullOrWhiteSpace(response.ProviderName))
        {
            sb.AppendLine($"-- Provider:  {response.ProviderName}");
        }

        sb.AppendLine();
        sb.Append(rawSql);
        return sb.ToString();
    }

    private static string StripQueryLensMetadataComment(string markdown)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            markdown,
            @"\n?<!--\s*QUERYLENS_META:[A-Za-z0-9+/=]+\s*-->\n?",
            "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static (string? HeaderLine, string BodyMarkdown) ExtractPinnedHeaderAndBody(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var outputLines = new List<string>(lines.Length);
        string? headerLine = null;
        var inCodeFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                outputLines.Add(line);
                continue;
            }

            if (headerLine is null
                && !inCodeFence
                && IsQueryLensHeaderLine(line))
            {
                headerLine = line;
                Log($"hover-header-match line={i + 1} value='{TruncateForLog(line, 180)}'");
                continue;
            }

            outputLines.Add(line);
        }

        var bodyMarkdown = string.Join("\n", outputLines);
        if (headerLine is null)
        {
            var firstNonEmpty = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            Log($"hover-header-miss firstNonEmpty='{TruncateForLog(firstNonEmpty, 180)}'");
        }

        return (headerLine, bodyMarkdown);
    }

    private static IEnumerable<FrameworkElement> ParseMarkdown(string markdown, string? preferredCopySql, bool suppressHeaderLines = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var elements = new List<FrameworkElement>();

        var inCodeFence = false;
        var codeFenceLanguage = string.Empty;
        var codeLines = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw;

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCodeFence)
                {
                    inCodeFence = true;
                    codeFenceLanguage = line.Length > 3 ? line.Substring(3).Trim().ToLowerInvariant() : string.Empty;
                    codeLines.Clear();
                }
                else
                {
                    elements.Add(RenderCodeBlock(codeFenceLanguage, codeLines));
                    inCodeFence = false;
                    codeFenceLanguage = string.Empty;
                    codeLines.Clear();
                }

                continue;
            }

            if (inCodeFence)
            {
                codeLines.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                elements.Add(new Border { Height = 6, Background = Brushes.Transparent });
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                elements.Add(RenderHeading(line.Substring(4), 14, preferredCopySql));
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                elements.Add(RenderHeading(line.Substring(3), 16, preferredCopySql));
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                elements.Add(RenderHeading(line.Substring(2), 18, preferredCopySql));
                continue;
            }

            if (IsQueryLensHeaderLine(line))
            {
                if (suppressHeaderLines)
                {
                    continue;
                }

                elements.Add(RenderHeaderLine(line, preferredCopySql));
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                elements.Add(RenderBullet(line.Substring(2), preferredCopySql));
                continue;
            }

            elements.Add(RenderParagraph(line, preferredCopySql));
        }

        if (inCodeFence && codeLines.Count > 0)
        {
            elements.Add(RenderCodeBlock(codeFenceLanguage, codeLines));
        }

        return elements;
    }

    private static FrameworkElement RenderHeading(string text, double size, string? preferredCopySql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var tb = new TextBlock
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

    private static bool IsQueryLensHeaderLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var candidate = line.Trim();

        // Strip common markdown prefixes before checking for the QueryLens heading.
        while (candidate.Length > 0)
        {
            var ch = candidate[0];
            if (ch == '#'
                || ch == '>'
                || ch == '-'
                || ch == '*'
                || ch == '_'
                || ch == '`'
                || ch == ' '
                || ch == '\t')
            {
                candidate = candidate.Substring(1).TrimStart();
                continue;
            }

            break;
        }

        if (!candidate.StartsWith("QueryLens", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Avoid matching inline prose that merely mentions QueryLens.
        if (candidate.Length > "QueryLens".Length)
        {
            var next = candidate["QueryLens".Length];
            if (!char.IsWhiteSpace(next) && next != '|' && next != '·' && next != ':')
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildFallbackHeaderLine(string? sqlForActions)
    {
        if (string.IsNullOrWhiteSpace(sqlForActions))
        {
            return "**QueryLens SQL Preview**";
        }

        return "**QueryLens SQL Preview** | [Copy SQL](efquerylens://copySql) | [Open SQL Editor](efquerylens://openSqlEditor)";
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sanitized = (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
        return sanitized.Length <= maxLength
            ? sanitized
            : sanitized.Substring(0, maxLength) + "...";
    }

    private static void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] [Renderer] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch
        {
            // Ignore logging failures.
        }
    }

    private static FrameworkElement RenderParagraph(string text, string? preferredCopySql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var tb = new TextBlock
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

    private static FrameworkElement RenderHeaderLine(string text, string? preferredCopySql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var tb = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
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
        var panel = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var bullet = new TextBlock
        {
            Text = "• ",
            Width = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4)),
            FontFamily = new FontFamily("Segoe UI"),
        };
        var content = new TextBlock
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
        var codeStack = new StackPanel { Margin = new Thickness(0) };

        var languageLabel = string.IsNullOrWhiteSpace(language)
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

        foreach (var line in lines.DefaultIfEmpty(string.Empty))
        {
            var displayLine = line.Replace("\\`", "`");
            var tb = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 0),
                TextWrapping = TextWrapping.NoWrap,
            };

            if (string.Equals(language, "sql", StringComparison.OrdinalIgnoreCase))
            {
                ApplySqlSyntaxHighlight(tb, displayLine);
                codeStack.Children.Add(tb);
                continue;
            }

            tb.Text = displayLine;

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
                else
                {
                    tb.Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4));
                }
            }
            else
            {
                tb.Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4));
            }

            codeStack.Children.Add(tb);
        }

        var innerScrollViewer = new ScrollViewer
        {
            Content = codeStack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        innerScrollViewer.PreviewMouseWheel += ForwardMouseWheelToOuterScrollViewer;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
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
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
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

    private static void ApplySqlSyntaxHighlight(TextBlock target, string line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        static bool IsIdentifierChar(char ch)
            => char.IsLetterOrDigit(ch) || ch == '_';

        static bool IsKeyword(string token)
        {
            return sqlKeywords.Contains(token);
        }

        var defaultBrush = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4));
        var keywordBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x9c, 0xd6));
        var numberBrush = new SolidColorBrush(Color.FromRgb(0xb5, 0xce, 0xa8));
        var stringBrush = new SolidColorBrush(Color.FromRgb(0xce, 0x91, 0x78));
        var identifierBrush = new SolidColorBrush(Color.FromRgb(0x9c, 0xdc, 0xfe));
        var commentBrush = new SolidColorBrush(Color.FromRgb(0x6a, 0x99, 0x55));

        var i = 0;
        while (i < line.Length)
        {
            if (i + 1 < line.Length && line[i] == '-' && line[i + 1] == '-')
            {
                target.Inlines.Add(new Run(line.Substring(i)) { Foreground = commentBrush });
                break;
            }

            if (line[i] == '`')
            {
                var end = line.IndexOf('`', i + 1);
                if (end < 0)
                {
                    end = line.Length - 1;
                }

                var len = end - i + 1;
                target.Inlines.Add(new Run(line.Substring(i, len)) { Foreground = identifierBrush });
                i += len;
                continue;
            }

            if (line[i] == '\'')
            {
                var sb = new StringBuilder();
                sb.Append(line[i]);
                i++;
                while (i < line.Length)
                {
                    sb.Append(line[i]);
                    if (line[i] == '\'' && (i + 1 >= line.Length || line[i + 1] != '\''))
                    {
                        i++;
                        break;
                    }

                    if (line[i] == '\'' && i + 1 < line.Length && line[i + 1] == '\'')
                    {
                        i++;
                        sb.Append(line[i]);
                    }

                    i++;
                }

                target.Inlines.Add(new Run(sb.ToString()) { Foreground = stringBrush });
                continue;
            }

            if (char.IsDigit(line[i]))
            {
                var start = i;
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.'))
                {
                    i++;
                }

                target.Inlines.Add(new Run(line.Substring(start, i - start)) { Foreground = numberBrush });
                continue;
            }

            if (IsIdentifierChar(line[i]))
            {
                var start = i;
                while (i < line.Length && IsIdentifierChar(line[i]))
                {
                    i++;
                }

                var token = line.Substring(start, i - start);
                target.Inlines.Add(new Run(token)
                {
                    Foreground = IsKeyword(token) ? keywordBrush : defaultBrush,
                    FontWeight = IsKeyword(token) ? FontWeights.SemiBold : FontWeights.Normal,
                });
                continue;
            }

            target.Inlines.Add(new Run(line[i].ToString()) { Foreground = defaultBrush });
            i++;
        }
    }

    private static void AppendInlineMarkdown(InlineCollection inlines, string text, string? preferredCopySql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        text = text.Replace(" | ", "  •  ");

        var i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    var strong = text.Substring(i + 2, end - (i + 2));
                    inlines.Add(new Run(strong) { FontWeight = FontWeights.SemiBold });
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    var code = text.Substring(i + 1, end - (i + 1));
                    inlines.Add(new Run(code)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = new SolidColorBrush(Color.FromRgb(0xd7, 0xba, 0x7d)),
                    });
                    i = end + 1;
                    continue;
                }
            }

            if (text[i] == '[')
            {
                var closeBracket = text.IndexOf(']', i + 1);
                if (closeBracket > i + 1 && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                {
                    var closeParen = text.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket + 2)
                    {
                        var label = text.Substring(i + 1, closeBracket - (i + 1));
                        var target = text.Substring(closeBracket + 2, closeParen - (closeBracket + 2));
                        var hyperlink = new Hyperlink(new Run(label))
                        {
                            Foreground = new SolidColorBrush(Color.FromRgb(0x68, 0xc4, 0xff)),
                        };
                        hyperlink.Click += (_, _) =>
                        {
                            ThreadHelper.ThrowIfNotOnUIThread();
                            HandleMarkdownLinkClick(target, preferredCopySql);
                        };
                        inlines.Add(hyperlink);
                        i = closeParen + 1;
                        continue;
                    }
                }
            }

            var next = FindNextInlineToken(text, i);
            var length = Math.Max(1, next - i);
            inlines.Add(new Run(text.Substring(i, length)));
            i += length;
        }
    }

    private static int FindNextInlineToken(string text, int start)
    {
        var next = text.Length;
        var strong = text.IndexOf("**", start, StringComparison.Ordinal);
        if (strong >= 0)
        {
            next = Math.Min(next, strong);
        }

        var backtick = text.IndexOf('`', start);
        if (backtick >= 0)
        {
            next = Math.Min(next, backtick);
        }

        var bracket = text.IndexOf('[', start);
        if (bracket >= 0)
        {
            next = Math.Min(next, bracket);
        }

        return next;
    }

    private static void HandleMarkdownLinkClick(string target, string? enrichedSql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!string.IsNullOrWhiteSpace(target)
            && Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, "efquerylens", StringComparison.OrdinalIgnoreCase))
        {
            var host = uri.Host.ToLowerInvariant();

            if (host == "copysql" && !string.IsNullOrWhiteSpace(enrichedSql))
            {
                Clipboard.SetText(enrichedSql);
                return;
            }

            if (host == "opensqleditor" && !string.IsNullOrWhiteSpace(enrichedSql))
            {
                TryOpenSqlInEditor(enrichedSql!);
                return;
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(target)
            && Uri.TryCreate(target, UriKind.Absolute, out var external)
            && (string.Equals(external.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(external.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(external.ToString()) { UseShellExecute = true });
            }
            catch
            {
                // Ignore failures to open external links.
            }
        }
    }

    private static void TryOpenSqlInEditor(string content)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var tempDir = Path.Combine(Path.GetTempPath(), "EFQueryLens");
        Directory.CreateDirectory(tempDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var tempPath = Path.Combine(tempDir, $"preview_{stamp}.sql");
        File.WriteAllText(tempPath, content, Encoding.UTF8);

        try
        {
            if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
            {
                dte.ItemOperations.OpenFile(tempPath);
                return;
            }
        }
        catch
        {
            // Fall through to process-start fallback.
        }

        try
        {
            Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
        }
        catch
        {
            // Ignore.
        }
    }

    private static QueryLensHoverMetadata? TryExtractQueryLensMetadata(string markdown)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            markdown, @"<!--\s*QUERYLENS_META:([A-Za-z0-9+/=]+)\s*-->",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));
            return JsonSerializer.Deserialize<QueryLensHoverMetadata>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildEnrichedSqlContent(string? rawSql, QueryLensHoverMetadata? metadata)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
            return null;
        }

        if (metadata is null)
        {
            return rawSql;
        }

        var sb = new StringBuilder();
        sb.AppendLine("-- EF QueryLens");

        if (!string.IsNullOrWhiteSpace(metadata.SourceFile))
        {
            var lineDisplay = metadata.SourceLine > 0 ? $", line {metadata.SourceLine}" : string.Empty;
            sb.AppendLine($"-- Source:    {metadata.SourceFile}{lineDisplay}");
        }

        AppendCommentedExpression(sb, "LINQ", metadata.SourceExpression);

        if (!string.IsNullOrWhiteSpace(metadata.ExecutedExpression)
            && metadata.ExecutedExpression != metadata.SourceExpression)
        {
            AppendCommentedExpression(sb, "Executed LINQ (differs from source)", metadata.ExecutedExpression);
        }

        if (!string.IsNullOrWhiteSpace(metadata.DbContextType))
        {
            sb.AppendLine($"-- DbContext: {metadata.DbContextType}");
        }

        if (!string.IsNullOrWhiteSpace(metadata.ProviderName))
        {
            sb.AppendLine($"-- Provider:  {metadata.ProviderName}");
        }

        if (!string.IsNullOrWhiteSpace(metadata.CreationStrategy))
        {
            sb.AppendLine($"-- Strategy:  {metadata.CreationStrategy}");
        }

        if (metadata.Warnings is { Count: > 0 })
        {
            sb.AppendLine("-- Notes:");
            foreach (var w in metadata.Warnings)
            {
                sb.AppendLine($"--   {w}");
            }
        }

        sb.AppendLine();
        sb.Append(rawSql);
        return sb.ToString();
    }

    private static void AppendCommentedExpression(StringBuilder sb, string label, string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return;
        }

        sb.AppendLine($"-- {label}:");
        foreach (var exprLine in expression!.Replace("\r\n", "\n").Split('\n'))
        {
            sb.AppendLine(exprLine.Length == 0 ? "--" : $"--   {exprLine.TrimEnd()}");
        }
    }

    private static string? TryExtractFirstCodeBlock(string markdown, string preferredLanguage)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inCode = false;
        var language = string.Empty;
        var codeLines = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw;
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCode)
                {
                    inCode = true;
                    language = line.Length > 3 ? line.Substring(3).Trim() : string.Empty;
                    codeLines.Clear();
                }
                else
                {
                    if ((string.IsNullOrWhiteSpace(preferredLanguage)
                            || string.Equals(language, preferredLanguage, StringComparison.OrdinalIgnoreCase))
                        && codeLines.Count > 0)
                    {
                        return string.Join(Environment.NewLine, codeLines);
                    }

                    inCode = false;
                    language = string.Empty;
                    codeLines.Clear();
                }

                continue;
            }

            if (inCode)
            {
                codeLines.Add(line);
            }
        }

        return null;
    }

    private static string BuildMarkdown(string? linqCode)
    {
        var safeQuery = (linqCode ?? string.Empty).Replace("\r\n", "\n").Trim();
        var plusQuery = string.Join("\n", safeQuery.Split('\n').Select(l => $"+ {l}"));

        return $@"# LINQ Hover Documentation
This tooltip appears when you hover over a LINQ query.

## Query Diff Preview
```diff
- // query before transformation
{plusQuery}
```

## SQL examples
```sql
SELECT c.Id, c.Name
FROM Customers c
WHERE c.IsActive = 1
ORDER BY c.Name;
```

```sql
SELECT o.CustomerId, COUNT(*) AS OrderCount
FROM Orders o
GROUP BY o.CustomerId
HAVING COUNT(*) > 5;
```

### Notes
- Hover works across LINQ chains and related symbols in the same query.
- Use More... for the larger scrollable documentation window.
";
    }
}

