import {
    commands,
    env,
    Position,
    Range,
    Selection,
    TextEditorRevealType,
    ViewColumn,
    WebviewPanel,
    window,
    workspace,
} from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';

import { formatSql } from '../sql/formatting';
import { QueryLensSqlDialect, QueryLensStructuredHoverResponse } from '../types';
import { formatUserMessage } from '../utils/errors';
import { clamp, coerceNonNegativeInt, parseUri } from '../utils/parsing';

export type SqlActionHandlers = {
    showSqlPopupFromLens(uriInput: unknown, lineInput: unknown, characterInput: unknown): Promise<void>;
    recalculatePreviewFromLens(uriInput: unknown, lineInput: unknown, characterInput: unknown): Promise<void>;
    copySqlFromLens(
        uriInput: unknown,
        lineInput: unknown,
        characterInput: unknown,
        formatSqlOnShow: boolean,
        sqlDialect: QueryLensSqlDialect
    ): Promise<void>;
    openSqlEditorFromLens(
        uriInput: unknown,
        lineInput: unknown,
        characterInput: unknown,
        formatSqlOnShow: boolean,
        sqlDialect: QueryLensSqlDialect
    ): Promise<void>;
};

export function createSqlActionHandlers(getClient: () => LanguageClient | undefined): SqlActionHandlers {
    let sqlPreviewPanel: WebviewPanel | undefined;

    async function showSqlPopupFromLens(
        uriInput: unknown,
        lineInput: unknown,
        characterInput: unknown
    ): Promise<void> {
        const uri = parseUri(uriInput);
        if (!uri) {
            window.showWarningMessage(formatUserMessage('QL1002_INVALID_URI', 'Unable to resolve document URI for SQL preview.'));
            return;
        }

        const document = await workspace.openTextDocument(uri);
        const editor = await window.showTextDocument(document, {
            preview: false,
            preserveFocus: false,
        });

        const requestedLine = coerceNonNegativeInt(lineInput, 0);
        const requestedCharacter = coerceNonNegativeInt(characterInput, 0);
        const line = clamp(requestedLine, 0, Math.max(document.lineCount - 1, 0));
        const lineText = document.lineAt(line).text;
        const character = clamp(requestedCharacter, 0, lineText.length);
        const position = new Position(line, character);

        editor.selection = new Selection(position, position);
        editor.revealRange(new Range(position, position), TextEditorRevealType.InCenterIfOutsideViewport);

        await commands.executeCommand('editor.action.showHover');
    }

    async function recalculatePreviewFromLens(
        uriInput: unknown,
        lineInput: unknown,
        characterInput: unknown
    ): Promise<void> {
        const client = getClient();
        if (!client) {
            window.showWarningMessage(formatUserMessage('QL1005_DAEMON_RESTART_NOT_READY', 'Language client is not initialized yet.'));
            return;
        }

        const uri = parseUri(uriInput);
        if (!uri) {
            window.showWarningMessage(formatUserMessage('QL1002_INVALID_URI', 'Unable to resolve document URI for SQL preview.'));
            return;
        }

        try {
            const line = coerceNonNegativeInt(lineInput, 0);
            const character = coerceNonNegativeInt(characterInput, 0);

            const response = await client.sendRequest<unknown>('efquerylens/preview/recalculate', {
                textDocument: { uri: uri.toString() },
                position: { line, character },
            });

            const success = !!(response && typeof response === 'object' && (response as { success?: unknown }).success === true);
            const message = response && typeof response === 'object' && typeof (response as { message?: unknown }).message === 'string'
                ? (response as { message: string }).message
                : (success ? 'Preview recalculated.' : 'Preview recalculation did not complete.');

            if (!success) {
                window.showWarningMessage(`EF QueryLens: ${message}`);
                return;
            }

            await showSqlPopupFromLens(uri.toString(), line, character);
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            window.showErrorMessage(formatUserMessage('QL1004_HOVER_REQUEST_FAILED', `Failed to recalculate SQL preview. ${message}`));
        }
    }

    async function copySqlFromLens(
        uriInput: unknown,
        lineInput: unknown,
        characterInput: unknown,
        formatSqlOnShow: boolean,
        sqlDialect: QueryLensSqlDialect
    ): Promise<void> {
        const sqlText = await getSqlForLens(
            uriInput,
            lineInput,
            characterInput,
            formatSqlOnShow,
            sqlDialect,
            true);
        if (!sqlText) {
            return;
        }

        await env.clipboard.writeText(sqlText);
        window.showInformationMessage('EF QueryLens: SQL copied to clipboard.');
    }

    async function openSqlEditorFromLens(
        uriInput: unknown,
        lineInput: unknown,
        characterInput: unknown,
        formatSqlOnShow: boolean,
        sqlDialect: QueryLensSqlDialect
    ): Promise<void> {
        const preview = await getSqlPreviewForLens(
            uriInput,
            lineInput,
            characterInput,
            formatSqlOnShow,
            sqlDialect
        );
        if (!preview) {
            return;
        }

        openSqlPreviewPanelForLens(preview);
    }

    function openSqlPreviewPanelForLens(preview: {
        title: string;
        subtitle: string;
        statusCode: number;
        statusText: string;
        avgTranslationMs: number;
        sqlText: string;
    }): void {
        if (!sqlPreviewPanel) {
            sqlPreviewPanel = window.createWebviewPanel(
                'efquerylensSqlPreview',
                'EF QueryLens SQL Preview',
                ViewColumn.Beside,
                {
                    enableScripts: false,
                    retainContextWhenHidden: true,
                }
            );

            sqlPreviewPanel.onDidDispose(() => {
                sqlPreviewPanel = undefined;
            });
        }

        sqlPreviewPanel.title = 'EF QueryLens SQL Preview';
        sqlPreviewPanel.webview.html = buildSqlPreviewHtml(
            preview.title,
            preview.subtitle,
            preview.statusCode,
            preview.statusText,
            preview.avgTranslationMs,
            preview.sqlText
        );
        sqlPreviewPanel.reveal(ViewColumn.Beside, false);
    }

    async function getSqlPreviewForLens(
        uriInput: unknown,
        lineInput: unknown,
        characterInput: unknown,
        formatSqlOnShow: boolean,
        sqlDialect: QueryLensSqlDialect
    ): Promise<{ title: string; subtitle: string; statusCode: number; statusText: string; avgTranslationMs: number; sqlText: string } | null> {
        const client = getClient();
        if (!client) {
            return null;
        }

        const uri = parseUri(uriInput);
        if (!uri) {
            window.showWarningMessage(formatUserMessage('QL1002_INVALID_URI', 'Unable to resolve document URI for SQL preview.'));
            return null;
        }

        try {
            const line = coerceNonNegativeInt(lineInput, 0);
            const character = coerceNonNegativeInt(characterInput, 0);
            const structured = await tryGetStructuredHover(client, uri.toString(), line, character);
            if (!structured) {
                window.showWarningMessage('EF QueryLens structured hover response is unavailable.');
                return null;
            }

            if (structured.Status !== 0) {
                const statusMessage = structured.StatusMessage
                    ?? (structured.Status === 3
                        ? 'EF QueryLens services are unavailable and cannot communicate right now.'
                        : structured.Status === 2
                            ? 'EF QueryLens is starting up and warming translation services.'
                            : 'EF QueryLens queued this query and is still processing it.');

                if (structured.Status === 3) {
                    window.showWarningMessage(statusMessage);
                } else {
                    window.showInformationMessage(statusMessage);
                }

                return null;
            }

            if (!structured.Success) {
                const message = structured.ErrorMessage ?? 'No SQL preview available at this location.';
                window.showInformationMessage(formatUserMessage('QL1003_HOVER_EMPTY', message));
                return null;
            }

            const statementSql = (structured.Statements ?? [])
                .map((stmt, index, statements) => {
                    const sql = stmt.Sql ?? '';
                    if (statements.length <= 1) {
                        return sql;
                    }

                    const splitLabel = stmt.SplitLabel?.trim().length
                        ? stmt.SplitLabel
                        : `Split Query ${index + 1} of ${statements.length}`;
                    return `-- ${splitLabel}\n${sql}`;
                })
                .filter(sql => sql.trim().length > 0)
                .join('\n\n');

            const rawSql = statementSql || structured.EnrichedSql;
            if (!rawSql) {
                window.showWarningMessage('EF QueryLens structured hover payload is missing SQL text.');
                return null;
            }

            const formattedSql = formatSqlOnShow ? formatSql(rawSql, sqlDialect) : rawSql;
            const commandCount = structured.CommandCount > 0
                ? structured.CommandCount
                : Math.max(structured.Statements?.length ?? 0, 1);
            const statementWord = commandCount === 1 ? 'query' : 'queries';
            const title = `QueryLens · ${commandCount} ${statementWord} · ready`;
            const sourceFile = structured.SourceFile || uri.fsPath || uri.toString();
            const sourceLine = structured.SourceLine > 0 ? structured.SourceLine : line + 1;
            const provider = structured.ProviderName ? ` · ${structured.ProviderName}` : '';
            const subtitle = `${sourceFile}:${sourceLine}${provider}`;
            const statusText = 'ready';
            const avgTranslationMs = structured.AvgTranslationMs > 0 ? structured.AvgTranslationMs : 0;

            return {
                title,
                subtitle,
                statusCode: structured.Status,
                statusText,
                avgTranslationMs,
                sqlText: formattedSql,
            };
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            window.showErrorMessage(formatUserMessage('QL1004_HOVER_REQUEST_FAILED', `Failed to retrieve SQL preview. ${message}`));
            return null;
        }
    }

    function buildSqlPreviewHtml(
        title: string,
        subtitle: string,
        statusCode: number,
        statusText: string,
        avgTranslationMs: number,
        sqlText: string
    ): string {
        const safeTitle = escapeHtml(title);
        const safeSubtitle = escapeHtml(subtitle);
        const statusClass = toStatusClass(statusCode);
        const safeStatus = escapeHtml(statusText.toUpperCase());
        const safeAvg = avgTranslationMs > 0
            ? escapeHtml(`avg ${Math.round(avgTranslationMs)} ms`)
            : '';
        const safeSql = escapeHtml(sqlText);

        return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <style>
    :root {
      color-scheme: light dark;
    }

    body {
      margin: 0;
      padding: 0;
      font-family: var(--vscode-font-family);
      color: var(--vscode-editor-foreground);
      background: var(--vscode-editor-background);
      height: 100vh;
      overflow: hidden;
      display: flex;
      flex-direction: column;
    }

    .header {
      position: sticky;
      top: 0;
      z-index: 10;
      border-bottom: 1px solid var(--vscode-panel-border);
      background: linear-gradient(180deg, var(--vscode-sideBar-background), var(--vscode-editor-background));
      padding: 10px 14px;
    }

    .title {
      font-size: 13px;
      font-weight: 700;
      margin-bottom: 3px;
    }

    .subtitle {
      font-size: 11px;
      color: var(--vscode-descriptionForeground);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

        .meta {
            margin-top: 8px;
            display: flex;
            align-items: center;
            gap: 8px;
        }

        .statusChip {
            border: 1px solid var(--vscode-inputOption-activeBorder, var(--vscode-panel-border));
            border-radius: 999px;
            padding: 2px 8px;
            font-size: 10px;
            font-weight: 700;
            letter-spacing: 0.04em;
            color: var(--vscode-inputOption-activeForeground, var(--vscode-editor-foreground));
            background: var(--vscode-inputOption-activeBackground, transparent);
        }

        .statusChip.ready {
            border-color: color-mix(in srgb, #2ea043 60%, var(--vscode-panel-border));
            color: #2ea043;
            background: color-mix(in srgb, #2ea043 12%, transparent);
        }

        .statusChip.queued {
            border-color: color-mix(in srgb, #0969da 60%, var(--vscode-panel-border));
            color: #0969da;
            background: color-mix(in srgb, #0969da 12%, transparent);
        }

        .statusChip.starting {
            border-color: color-mix(in srgb, #bc4c00 60%, var(--vscode-panel-border));
            color: #bc4c00;
            background: color-mix(in srgb, #bc4c00 12%, transparent);
        }

        .statusChip.unavailable {
            border-color: color-mix(in srgb, #cf222e 60%, var(--vscode-panel-border));
            color: #cf222e;
            background: color-mix(in srgb, #cf222e 12%, transparent);
        }

        .avg {
            font-size: 11px;
            color: var(--vscode-descriptionForeground);
        }

        @supports not (color: color-mix(in srgb, black 50%, white)) {
            .statusChip.ready { border-color: #2ea043; color: #2ea043; background: rgba(46, 160, 67, 0.12); }
            .statusChip.queued { border-color: #0969da; color: #0969da; background: rgba(9, 105, 218, 0.12); }
            .statusChip.starting { border-color: #bc4c00; color: #bc4c00; background: rgba(188, 76, 0, 0.12); }
            .statusChip.unavailable { border-color: #cf222e; color: #cf222e; background: rgba(207, 34, 46, 0.12); }
        }

    .body {
      flex: 1;
      overflow: auto;
      padding: 0;
    }

    pre {
      margin: 0;
      padding: 14px;
      font-family: var(--vscode-editor-font-family, Consolas, "Courier New", monospace);
      font-size: 12px;
      line-height: 1.45;
      white-space: pre;
      tab-size: 2;
    }
  </style>
</head>
<body>
  <div class="header">
    <div class="title">${safeTitle}</div>
    <div class="subtitle">${safeSubtitle}</div>
        <div class="meta">
            <span class="statusChip ${statusClass}">${safeStatus}</span>
            ${safeAvg ? `<span class="avg">${safeAvg}</span>` : ''}
        </div>
  </div>
  <div class="body">
    <pre>${safeSql}</pre>
  </div>
</body>
</html>`;
    }

    function toStatusClass(statusCode: number): 'ready' | 'queued' | 'starting' | 'unavailable' {
        switch (statusCode) {
            case 1:
                return 'queued';
            case 2:
                return 'starting';
            case 3:
                return 'unavailable';
            default:
                return 'ready';
        }
    }

    function escapeHtml(value: string): string {
        return value
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    async function getSqlForLens(
        uriInput: unknown,
        lineInput: unknown,
        characterInput: unknown,
        formatSqlOnShow: boolean,
        sqlDialect: QueryLensSqlDialect,
        includeContextComments: boolean
    ): Promise<string | null> {
        const client = getClient();
        if (!client) {
            return null;
        }

        const uri = parseUri(uriInput);
        if (!uri) {
            window.showWarningMessage(formatUserMessage('QL1002_INVALID_URI', 'Unable to resolve document URI for SQL preview.'));
            return null;
        }

        try {
            const line = coerceNonNegativeInt(lineInput, 0);
            const character = coerceNonNegativeInt(characterInput, 0);
            const uriString = uri.toString();

            const structured = await tryGetStructuredHover(client, uriString, line, character);
            if (!structured) {
                window.showWarningMessage('EF QueryLens structured hover response is unavailable.');
                return null;
            }

            if (structured.Status !== 0) {
                const statusMessage = structured.StatusMessage
                    ?? (structured.Status === 3
                        ? 'EF QueryLens services are unavailable and cannot communicate right now.'
                        : structured.Status === 2
                            ? 'EF QueryLens is starting up and warming translation services.'
                            : 'EF QueryLens queued this query and is still processing it.');

                if (structured.Status === 3) {
                    window.showWarningMessage(statusMessage);
                } else {
                    window.showInformationMessage(statusMessage);
                }

                return null;
            }

            if (!structured.Success) {
                const message = structured.ErrorMessage ?? 'No SQL preview available at this location.';
                window.showInformationMessage(formatUserMessage('QL1003_HOVER_EMPTY', message));
                return null;
            }

            if (!structured.EnrichedSql) {
                window.showWarningMessage('EF QueryLens structured hover payload is missing EnrichedSql.');
                return null;
            }

            if (structured.EnrichedSql) {
                const rawSql = structured.Statements?.[0]?.Sql ?? structured.EnrichedSql;
                const formattedSql = formatSqlOnShow ? formatSql(rawSql, sqlDialect) : rawSql;
                return includeContextComments ? structured.EnrichedSql : formattedSql;
            }

            return null;
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            window.showErrorMessage(formatUserMessage('QL1004_HOVER_REQUEST_FAILED', `Failed to retrieve SQL preview. ${message}`));
            return null;
        }
    }

    async function tryGetStructuredHover(
        client: LanguageClient,
        uriString: string,
        line: number,
        character: number
    ): Promise<QueryLensStructuredHoverResponse | null> {
        try {
            const response = await client.sendRequest<QueryLensStructuredHoverResponse | null>(
                'efquerylens/hover',
                {
                    textDocument: { uri: uriString },
                    position: { line, character },
                }
            );
            return response ?? null;
        } catch {
            return null;
        }
    }

    return {
        showSqlPopupFromLens,
        recalculatePreviewFromLens,
        copySqlFromLens,
        openSqlEditorFromLens,
    };
}
