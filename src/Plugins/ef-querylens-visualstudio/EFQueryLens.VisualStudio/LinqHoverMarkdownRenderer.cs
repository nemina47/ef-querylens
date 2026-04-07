// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace EFQueryLens.VisualStudio;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

internal static partial class LinqHoverMarkdownRenderer
{
    public static FrameworkElement CreateFromStructured(QueryLensStructuredHoverResponse response, string uri, int line, int character)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        int status = response.Status;
        bool isQueueStatus = status is 1 or 2;
        bool isServiceUnavailable = status is 3;

        if (!response.Success && !isQueueStatus && !isServiceUnavailable)
        {
            string errorMessage = response.ErrorMessage ?? "Translation failed.";
            response = new QueryLensStructuredHoverResponse
            {
                Success = false,
                ErrorMessage = errorMessage,
                Statements = [],
                CommandCount = 0,
                SourceExpression = response.SourceExpression,
                DbContextType = response.DbContextType,
                ProviderName = response.ProviderName,
                SourceFile = response.SourceFile,
                SourceLine = response.SourceLine,
                Warnings = response.Warnings,
                EnrichedSql = null,
                Mode = response.Mode,
                Status = 3,
                StatusMessage = errorMessage,
                AvgTranslationMs = response.AvgTranslationMs,
                LastTranslationMs = response.LastTranslationMs,
            };
            status = response.Status;
            isServiceUnavailable = true;
        }

        List<QueryLensSqlStatementDto> statements = response.Statements ?? [];
        string? enrichedSql = string.IsNullOrWhiteSpace(response.EnrichedSql)
            ? null
            : response.EnrichedSql;
        string? copySql = enrichedSql;
        double effectiveTranslationMs = response.LastTranslationMs > 0
            ? response.LastTranslationMs
            : response.AvgTranslationMs;

        string queryParams = $"uri={Uri.EscapeDataString(uri)}&line={line}&character={character}";
        string statementWord = response.CommandCount == 1 ? "query" : "queries";
        string statusLabel = BuildStructuredStatusLabel(status, response.AvgTranslationMs);
        string readyLabel = $"**EF QueryLens** · {response.CommandCount} {statementWord}";
        string actionsLine = $"[Copy SQL](efquerylens://copySql?{queryParams}) | [Open SQL](efquerylens://openSqlEditor?{queryParams}) | [Reanalyze](efquerylens://recalculate?{queryParams})";
        string headerText = status == 0
            ? (string.IsNullOrWhiteSpace(copySql)
                ? readyLabel
                : $"{readyLabel}\n{actionsLine}")
            : $"**{statusLabel}**";

        Border hostBorder = new()
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 380,
            MaxHeight = 420,
        };

        Grid layoutGrid = new();
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        FrameworkElement headerElement = RenderHeaderLine(headerText, copySql);
        Grid.SetRow(headerElement, 0);
        layoutGrid.Children.Add(headerElement);

        ScrollViewer scrollViewer = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        StackPanel stack = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        if (!response.Success
            && status is 1 or 2 or 3
            && (!string.IsNullOrWhiteSpace(response.StatusMessage) || !string.IsNullOrWhiteSpace(response.ErrorMessage)))
        {
            string statusMessage = response.StatusMessage ?? response.ErrorMessage ?? "Translation failed.";
            if (!statusMessage.StartsWith("EF QueryLens - error", StringComparison.OrdinalIgnoreCase))
            {
                stack.Children.Add(RenderParagraph(statusMessage, copySql));
            }
        }




        foreach (QueryLensSqlStatementDto stmt in statements)
        {
            List<string> sqlLines = (stmt.Sql ?? string.Empty).Replace("\r\n", "\n").Split('\n').ToList();
            string? rawSplitLabel = stmt.SplitLabel;
            if (!string.IsNullOrWhiteSpace(rawSplitLabel))
            {
                string label = rawSplitLabel!.Trim().Trim('*').Trim();
                if (!string.IsNullOrWhiteSpace(label))
                {
                    sqlLines.Insert(0, $"-- {label}");
                }
            }
            stack.Children.Add(RenderCodeBlock("sql", sqlLines));
        }

        List<string> warnings = response.Warnings ?? [];
        if (warnings.Count > 0)
        {
            stack.Children.Add(RenderHeading("Notes", 13, copySql));
            foreach (string w in warnings)
            {
                stack.Children.Add(RenderBullet(w, copySql));
            }
        }

        if (status == 0 && effectiveTranslationMs > 0)
        {
            stack.Children.Add(RenderSecondaryItalic($"SQL generation time {effectiveTranslationMs:0} ms", copySql));
        }

        scrollViewer.Content = stack;
        Grid.SetRow(scrollViewer, 1);
        layoutGrid.Children.Add(scrollViewer);
        hostBorder.Child = layoutGrid;

        return hostBorder;
    }

    private static string BuildStructuredStatusLabel(int status, double avgTranslationMs)
    {
        _ = avgTranslationMs;
        return status switch
        {
            0 => "EF QueryLens - ready",
            1 => "EF QueryLens - in queue",
            2 => "EF QueryLens - starting up",
            3 => "EF QueryLens - error",
            _ => "EF QueryLens - in queue",
        };
    }
}

