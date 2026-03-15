import {
    commands,
    env,
    Position,
    Range,
    Selection,
    TextEditorRevealType,
    window,
    workspace,
} from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';

import {
    extractHoverText,
    extractQueryLensMetadata,
    extractSqlBlocks,
    prependQueryLensContextComments,
} from '../hover/markdown';
import { formatSql } from '../sql/formatting';
import { QueryLensHoverMetadata, QueryLensSqlDialect, QueryLensStructuredHoverResponse } from '../types';
import { formatUserMessage } from '../utils/errors';
import { clamp, coerceNonNegativeInt, parseUri } from '../utils/parsing';

export type SqlActionHandlers = {
    showSqlPopupFromLens(uriInput: unknown, lineInput: unknown, characterInput: unknown): Promise<void>;
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

        await openSqlPreviewInEditor(sqlText);
    }

    async function openSqlPreviewInEditor(sqlText: string): Promise<void> {
        const document = await workspace.openTextDocument({
            language: 'sql',
            content: sqlText,
        });

        await window.showTextDocument(document, {
            preview: true,
            preserveFocus: false,
        });
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

            // Prefer structured hover which carries server-built EnrichedSql, avoiding
            // client-side markdown extraction + metadata decoding + enrichment rebuild.
            const structured = await tryGetStructuredHover(client, uriString, line, character);
            if (structured && structured.Status !== 0) {
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

            if (structured?.Success && structured.EnrichedSql) {
                const rawSql = structured.Statements?.[0]?.Sql ?? structured.EnrichedSql;
                const formattedSql = formatSqlOnShow ? formatSql(rawSql, sqlDialect) : rawSql;
                return includeContextComments ? structured.EnrichedSql : formattedSql;
            }

            // Markdown fallback for older daemon builds that don't return EnrichedSql.
            const hover = await client.sendRequest('textDocument/hover', {
                textDocument: { uri: uriString },
                position: { line, character },
            });

            const hoverText = extractHoverText(hover);
            if (!hoverText) {
                window.showInformationMessage(formatUserMessage('QL1003_HOVER_EMPTY', 'No SQL preview available at this location.'));
                return null;
            }

            const metadata = extractQueryLensMetadata(hoverText)
                ?? createFallbackMetadata(uriString, line);
            const sqlText = extractSqlBlocks(hoverText);
            if (sqlText) {
                const formattedSql = formatSqlOnShow ? formatSql(sqlText, sqlDialect) : sqlText;
                return includeContextComments
                    ? prependQueryLensContextComments(formattedSql, metadata)
                    : formattedSql;
            }

            return hoverText;
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

    function createFallbackMetadata(sourceUri: string, zeroBasedLine: number): QueryLensHoverMetadata {
        return {
            MetadataProvenance: 'fallback',
            SourceExpression: '',
            ExecutedExpression: '',
            Mode: 'direct',
            ModeDescription: '',
            Warnings: [],
            SourceFile: sourceUri,
            SourceLine: Math.max(1, zeroBasedLine + 1),
            DbContextType: '',
            ProviderName: '',
            CreationStrategy: '',
        };
    }

    return {
        showSqlPopupFromLens,
        copySqlFromLens,
        openSqlEditorFromLens,
    };
}
