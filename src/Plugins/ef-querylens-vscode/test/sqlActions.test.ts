import { beforeEach, describe, expect, test } from 'vitest';

import {
    getClipboardText,
    getDocumentInteractions,
    getExecutedCommands,
    getWindowMessages,
    resetVscodeMocks,
} from './mocks/vscode';
import { createSqlActionHandlers } from '../src/commands/sqlActions';

describe('sql action handlers', () => {
    beforeEach(() => {
        resetVscodeMocks();
    });

    test('showSqlPopupFromLens warns for invalid uri', async () => {
        const handlers = createSqlActionHandlers(() => undefined);

        await handlers.showSqlPopupFromLens('not a uri', 0, 0);

        const messages = getWindowMessages();
        expect(messages.warnings.some(m => m.includes('QL1002_INVALID_URI'))).toBe(true);
    });

    test('recalculatePreviewFromLens warns when client is missing', async () => {
        const handlers = createSqlActionHandlers(() => undefined);

        await handlers.recalculatePreviewFromLens('file:///c:/repo/query.cs', 10, 5);

        const messages = getWindowMessages();
        expect(messages.warnings.some(m => m.includes('QL1005_DAEMON_RESTART_NOT_READY'))).toBe(true);
    });

    test('recalculatePreviewFromLens success re-opens hover command', async () => {
        const handlers = createSqlActionHandlers(
            () =>
                ({
                    sendRequest: async (method: string) => {
                        if (method === 'efquerylens/preview/recalculate') {
                            return { success: true, message: 'ok' };
                        }

                        throw new Error(`unexpected method ${method}`);
                    },
                }) as never
        );

        await handlers.recalculatePreviewFromLens('file:///c:/repo/query.cs', 10, 5);

        const executed = getExecutedCommands();
        expect(executed.some(c => c.id === 'editor.action.showHover')).toBe(true);
    });

    test('copySqlFromLens writes enriched sql to clipboard and notifies user', async () => {
        const handlers = createSqlActionHandlers(
            () =>
                ({
                    sendRequest: async () =>
                        ({
                            Status: 0,
                            Success: true,
                            EnrichedSql: '/* enriched */\nSELECT 1',
                            Statements: [{ Sql: 'SELECT 1' }],
                            CommandCount: 1,
                            SourceLine: 10,
                            LastTranslationMs: 12,
                        }) as unknown,
                }) as never
        );

        await handlers.copySqlFromLens('file:///c:/repo/query.cs', 10, 5, true, 'auto');

        expect(getClipboardText().toLowerCase()).toContain('select');
        expect(getClipboardText()).toContain('1');
        const messages = getWindowMessages();
        expect(messages.infos.some(m => m.includes('SQL copied to clipboard'))).toBe(true);
    });

    test('openSqlEditorFromLens creates untitled document edit with enriched sql', async () => {
        const handlers = createSqlActionHandlers(
            () =>
                ({
                    sendRequest: async () =>
                        ({
                            Status: 0,
                            Success: true,
                            EnrichedSql: '-- metadata\nSELECT 2',
                            Statements: [{ Sql: 'SELECT 2' }],
                            CommandCount: 1,
                            SourceLine: 22,
                            LastTranslationMs: 9,
                        }) as unknown,
                }) as never
        );

        await handlers.openSqlEditorFromLens('file:///c:/repo/query.cs', 22, 3, true, 'sql');

        const interactions = getDocumentInteractions();
        expect(interactions.appliedEdits.length).toBe(1);
        expect(interactions.createdEdits.some(e => /select/i.test(e.text) && e.text.includes('2'))).toBe(true);
        expect(interactions.shownDocuments.length).toBe(1);
    });

    test('copySqlFromLens surfaces status messages for non-ready responses', async () => {
        const handlers = createSqlActionHandlers(
            () =>
                ({
                    sendRequest: async () =>
                        ({
                            Status: 2,
                            Success: false,
                            StatusMessage: 'warming now',
                            Statements: [],
                        }) as unknown,
                }) as never
        );

        await handlers.copySqlFromLens('file:///c:/repo/query.cs', 10, 5, true, 'auto');

        const messages = getWindowMessages();
        expect(messages.infos).toContain('warming now');
        expect(getClipboardText()).toBe('');
    });

    test('copySqlFromLens warns when structured hover request is unavailable', async () => {
        const handlers = createSqlActionHandlers(
            () =>
                ({
                    sendRequest: async () => {
                        throw new Error('transport unavailable');
                    },
                }) as never
        );

        await handlers.copySqlFromLens('file:///c:/repo/query.cs', 10, 5, true, 'auto');

        const messages = getWindowMessages();
        expect(messages.warnings.some(m => m.includes('structured hover response is unavailable'))).toBe(true);
        expect(getClipboardText()).toBe('');
    });

    test('copySqlFromLens shows warning when status is unavailable', async () => {
        const handlers = createSqlActionHandlers(
            () =>
                ({
                    sendRequest: async () =>
                        ({
                            Status: 3,
                            Success: false,
                            Statements: [],
                        }) as unknown,
                }) as never
        );

        await handlers.copySqlFromLens('file:///c:/repo/query.cs', 10, 5, true, 'auto');

        const messages = getWindowMessages();
        expect(messages.warnings.some(m => m.includes('services are unavailable'))).toBe(true);
    });

    test('copySqlFromLens shows hover empty message for unsuccessful ready status', async () => {
        const handlers = createSqlActionHandlers(
            () =>
                ({
                    sendRequest: async () =>
                        ({
                            Status: 0,
                            Success: false,
                            ErrorMessage: 'No SQL generated',
                            Statements: [],
                        }) as unknown,
                }) as never
        );

        await handlers.copySqlFromLens('file:///c:/repo/query.cs', 10, 5, true, 'auto');

        const messages = getWindowMessages();
        expect(messages.infos.some(m => m.includes('QL1003_HOVER_EMPTY'))).toBe(true);
    });

    test('copySqlFromLens warns when enriched sql is missing', async () => {
        const handlers = createSqlActionHandlers(
            () =>
                ({
                    sendRequest: async () =>
                        ({
                            Status: 0,
                            Success: true,
                            EnrichedSql: null,
                            Statements: [{ Sql: 'SELECT 1' }],
                        }) as unknown,
                }) as never
        );

        await handlers.copySqlFromLens('file:///c:/repo/query.cs', 10, 5, true, 'auto');

        const messages = getWindowMessages();
        expect(messages.warnings.some(m => m.includes('missing EnrichedSql'))).toBe(true);
    });

    test('recalculatePreviewFromLens warns when recalculation does not complete', async () => {
        const handlers = createSqlActionHandlers(
            () =>
                ({
                    sendRequest: async () =>
                        ({
                            success: false,
                            message: 'still queued',
                        }) as unknown,
                }) as never
        );

        await handlers.recalculatePreviewFromLens('file:///c:/repo/query.cs', 10, 5);

        const messages = getWindowMessages();
        expect(messages.warnings.some(m => m.includes('still queued'))).toBe(true);
    });

    test('recalculatePreviewFromLens shows error when request throws', async () => {
        const handlers = createSqlActionHandlers(
            () =>
                ({
                    sendRequest: async () => {
                        throw new Error('recalculate failed');
                    },
                }) as never
        );

        await handlers.recalculatePreviewFromLens('file:///c:/repo/query.cs', 10, 5);

        const messages = getWindowMessages();
        expect(messages.errors.some(m => m.includes('QL1004_HOVER_REQUEST_FAILED'))).toBe(true);
    });

    test('openSqlEditorFromLens does nothing when client is missing', async () => {
        const handlers = createSqlActionHandlers(() => undefined);

        await handlers.openSqlEditorFromLens('file:///c:/repo/query.cs', 22, 3, true, 'sql');

        const interactions = getDocumentInteractions();
        expect(interactions.appliedEdits.length).toBe(0);
        expect(interactions.shownDocuments.length).toBe(0);
    });
});
