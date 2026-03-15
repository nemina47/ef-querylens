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
import { QueryLensHoverMetadata, QueryLensSqlDialect } from '../types';
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
            window.showWarningMessage('EF QueryLens: unable to resolve document URI for SQL preview.');
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
            window.showWarningMessage('EF QueryLens: unable to resolve document URI for SQL preview.');
            return null;
        }

        try {
            const line = coerceNonNegativeInt(lineInput, 0);
            const character = coerceNonNegativeInt(characterInput, 0);
            const hover = await client.sendRequest('textDocument/hover', {
                textDocument: { uri: uri.toString() },
                position: {
                    line,
                    character,
                }
            });

            const hoverText = extractHoverText(hover);
            if (!hoverText) {
                window.showInformationMessage('EF QueryLens: no SQL preview available at this location.');
                return null;
            }

            const metadata = extractQueryLensMetadata(hoverText)
                ?? createFallbackMetadata(uri.toString(), line);
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
            window.showErrorMessage(`EF QueryLens: failed to retrieve SQL preview. ${message}`);
            return null;
        }
    }

    function createFallbackMetadata(sourceUri: string, zeroBasedLine: number): QueryLensHoverMetadata {
        return {
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
