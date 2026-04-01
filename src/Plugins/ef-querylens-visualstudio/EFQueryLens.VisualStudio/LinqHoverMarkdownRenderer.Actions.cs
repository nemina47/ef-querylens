// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

internal static partial class LinqHoverMarkdownRenderer
{
    private static readonly string logPath = Path.Combine(Path.GetTempPath(), "EFQueryLens.VisualStudio.log");

    private static string TruncateForLog(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string sanitized = (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
        return sanitized.Length <= maxLength
            ? sanitized
            : sanitized.Substring(0, maxLength) + "...";
    }

    private static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] [Renderer] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch
        {
            // Ignore logging failures.
        }
    }

    private static void AppendInlineMarkdown(InlineCollection inlines, string text, string? preferredCopySql)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    string strong = text.Substring(i + 2, end - (i + 2));
                    inlines.Add(new Run(strong) { FontWeight = FontWeights.SemiBold });
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    string code = text.Substring(i + 1, end - (i + 1));
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
                int closeBracket = text.IndexOf(']', i + 1);
                if (closeBracket > i + 1 && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                {
                    int closeParen = text.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket + 2)
                    {
                        string label = text.Substring(i + 1, closeBracket - (i + 1));
                        string target = text.Substring(closeBracket + 2, closeParen - (closeBracket + 2));
                        Hyperlink hyperlink = new(new Run(label))
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

            int next = FindNextInlineToken(text, i);
            int length = Math.Max(1, next - i);
            inlines.Add(new Run(text.Substring(i, length)));
            i += length;
        }
    }

    private static int FindNextInlineToken(string text, int start)
    {
        int next = text.Length;
        int strong = text.IndexOf("**", start, StringComparison.Ordinal);
        if (strong >= 0)
        {
            next = Math.Min(next, strong);
        }

        int backtick = text.IndexOf('`', start);
        if (backtick >= 0)
        {
            next = Math.Min(next, backtick);
        }

        int bracket = text.IndexOf('[', start);
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
            string host = uri.Host.ToLowerInvariant();

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

            if (host == "recalculate"
                && TryExtractHoverCommandArgs(uri, out string? documentUri, out int line, out int character))
            {
                _ = Task.Run(async delegate
                {
                    try
                    {
                        var result = await QueryLensLanguageClient.RequestPreviewRecalculateAsync(
                            documentUri,
                            line,
                            character,
                            default).ConfigureAwait(false);

                        Log($"hover-recalculate-link success={result.Success} message={TruncateForLog(result.Message, 180)}");
                    }
                    catch (Exception ex)
                    {
                        Log($"hover-recalculate-link exception={ex.GetType().Name} message={TruncateForLog(ex.Message, 180)}");
                    }
                });

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
        string tempDir = Path.Combine(Path.GetTempPath(), "EFQueryLens");
        Directory.CreateDirectory(tempDir);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string tempPath = Path.Combine(tempDir, $"efquery_{stamp}.sql");
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

    private static bool TryExtractHoverCommandArgs(Uri uri, out string documentUri, out int line, out int character)
    {
        documentUri = string.Empty;
        line = 0;
        character = 0;

        if (string.IsNullOrWhiteSpace(uri.Query))
        {
            return false;
        }

        foreach (string? part in uri.Query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split(new[] { '=' }, 2, StringSplitOptions.None);
            if (pair.Length != 2)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(pair[0]);
            string value = Uri.UnescapeDataString(pair[1]);

            if (key.Equals("uri", StringComparison.OrdinalIgnoreCase))
            {
                documentUri = value;
                continue;
            }

            if (key.Equals("line", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(value, out line);
                continue;
            }

            if (key.Equals("character", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(value, out character);
            }
        }

        return !string.IsNullOrWhiteSpace(documentUri);
    }
}
