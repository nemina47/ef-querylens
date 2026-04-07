// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

internal static partial class LinqHoverMarkdownRenderer
{
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

        private static readonly HashSet<string> csharpKeywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
            "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit",
            "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in",
            "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator",
            "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte",
            "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
            "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
            "volatile", "while", "var", "async", "await", "nameof", "when", "yield",
        };

        private static void ApplySqlSyntaxHighlight(TextBlock target, string line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            static bool IsIdentifierChar(char ch)
                => char.IsLetterOrDigit(ch) || ch == '_';

            static bool IsKeyword(string token)
            {
                return sqlKeywords.Contains(token);
            }

            SolidColorBrush defaultBrush = new(Color.FromRgb(0xd4, 0xd4, 0xd4));
            SolidColorBrush keywordBrush = new(Color.FromRgb(0x56, 0x9c, 0xd6));
            SolidColorBrush numberBrush = new(Color.FromRgb(0xb5, 0xce, 0xa8));
            SolidColorBrush stringBrush = new(Color.FromRgb(0xce, 0x91, 0x78));
            SolidColorBrush identifierBrush = new(Color.FromRgb(0x9c, 0xdc, 0xfe));
            SolidColorBrush commentBrush = new(Color.FromRgb(0x6a, 0x99, 0x55));

            int i = 0;
            while (i < line.Length)
            {
                if (i + 1 < line.Length && line[i] == '-' && line[i + 1] == '-')
                {
                    target.Inlines.Add(new Run(line.Substring(i)) { Foreground = commentBrush });
                    break;
                }

                if (line[i] == '`')
                {
                    int end = line.IndexOf('`', i + 1);
                    if (end < 0)
                    {
                        end = line.Length - 1;
                    }

                    int len = end - i + 1;
                    target.Inlines.Add(new Run(line.Substring(i, len)) { Foreground = identifierBrush });
                    i += len;
                    continue;
                }

                if (line[i] == '\'')
                {
                    StringBuilder sb = new();
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
                    int start = i;
                    while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.'))
                    {
                        i++;
                    }

                    target.Inlines.Add(new Run(line.Substring(start, i - start)) { Foreground = numberBrush });
                    continue;
                }

                if (IsIdentifierChar(line[i]))
                {
                    int start = i;
                    while (i < line.Length && IsIdentifierChar(line[i]))
                    {
                        i++;
                    }

                    string token = line.Substring(start, i - start);
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

        private static void ApplyCSharpSyntaxHighlight(TextBlock target, string line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            static bool IsIdentifierChar(char ch)
                => char.IsLetterOrDigit(ch) || ch == '_';

            static bool IsKeyword(string token)
            {
                return csharpKeywords.Contains(token);
            }

            SolidColorBrush defaultBrush = new(Color.FromRgb(0xd4, 0xd4, 0xd4));
            SolidColorBrush keywordBrush = new(Color.FromRgb(0x56, 0x9c, 0xd6));
            SolidColorBrush numberBrush = new(Color.FromRgb(0xb5, 0xce, 0xa8));
            SolidColorBrush stringBrush = new(Color.FromRgb(0xce, 0x91, 0x78));
            SolidColorBrush commentBrush = new(Color.FromRgb(0x6a, 0x99, 0x55));

            int i = 0;
            while (i < line.Length)
            {
                // Handle single-line comments
                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                {
                    target.Inlines.Add(new Run(line.Substring(i)) { Foreground = commentBrush });
                    break;
                }

                // Handle block comments
                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                {
                    int endComment = line.IndexOf("*/", i + 2);
                    int len = endComment >= 0 ? endComment - i + 2 : line.Length - i;
                    target.Inlines.Add(new Run(line.Substring(i, len)) { Foreground = commentBrush });
                    i += len;
                    continue;
                }

                // Handle string literals (double quotes)
                if (line[i] == '"')
                {
                    StringBuilder sb = new();
                    sb.Append(line[i]);
                    i++;
                    while (i < line.Length)
                    {
                        sb.Append(line[i]);
                        if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                        {
                            i++;
                            break;
                        }
                        i++;
                    }
                    target.Inlines.Add(new Run(sb.ToString()) { Foreground = stringBrush });
                    continue;
                }

                // Handle character literals
                if (line[i] == '\'')
                {
                    StringBuilder sb = new();
                    sb.Append(line[i]);
                    i++;
                    while (i < line.Length)
                    {
                        sb.Append(line[i]);
                        if (line[i] == '\'' && (i == 0 || line[i - 1] != '\\'))
                        {
                            i++;
                            break;
                        }
                        i++;
                    }
                    target.Inlines.Add(new Run(sb.ToString()) { Foreground = stringBrush });
                    continue;
                }

                // Handle numbers
                if (char.IsDigit(line[i]))
                {
                    int start = i;
                    while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == '_' || char.ToLowerInvariant(line[i]) == 'f' || char.ToLowerInvariant(line[i]) == 'd' || char.ToLowerInvariant(line[i]) == 'm'))
                    {
                        i++;
                    }
                    target.Inlines.Add(new Run(line.Substring(start, i - start)) { Foreground = numberBrush });
                    continue;
                }

                // Handle identifiers and keywords
                if (IsIdentifierChar(line[i]))
                {
                    int start = i;
                    while (i < line.Length && IsIdentifierChar(line[i]))
                    {
                        i++;
                    }

                    string token = line.Substring(start, i - start);
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
    }
