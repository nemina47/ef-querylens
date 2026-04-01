import { beforeEach, describe, expect, test } from 'vitest';

import {
    getExecutedCommands,
    getDocumentInteractions,
    getWindowMessages,
    resetVscodeMocks,
    setNextSaveDialogUri,
    setNextWarningChoice,
    Uri,
} from './mocks/vscode';
import { notifyNoFactoryFound, registerQueryLensCommands } from '../src/commands/registry';

describe('command registry', () => {
    beforeEach(() => {
        resetVscodeMocks();
    });

    test('notifyNoFactoryFound prompts once per assembly and can trigger generateFactory', async () => {
        setNextWarningChoice('Generate File');

        notifyNoFactoryFound('C:/app/MyApp.dll', 'My.Namespace.MyDbContext', () => undefined);
        notifyNoFactoryFound('C:/app/MyApp.dll', 'My.Namespace.MyDbContext', () => undefined);

        await Promise.resolve();

        const messages = getWindowMessages();
        expect(messages.warnings).toHaveLength(1);
        expect(messages.warnings[0]).toContain('No factory found for MyDbContext');

        const executed = getExecutedCommands().filter(c => c.id === 'efquerylens.generateFactory');
        expect(executed).toHaveLength(1);
        expect(executed[0].args[0]).toBe('C:/app/MyApp.dll');
        expect(executed[0].args[1]).toBe('My.Namespace.MyDbContext');
    });

    test('restart command reports success path', async () => {
        const logs: string[] = [];
        const disposables = registerQueryLensCommands({
            getSettings: () => ({
                maxCodeLensPerDocument: 50,
                codeLensDebounceMs: 250,
                codeLensUseModelFilter: false,
                formatSqlOnShow: true,
                sqlDialect: 'auto',
                debugLogsEnabled: false,
            }),
            sqlActions: {
                showSqlPopupFromLens: async () => undefined,
                recalculatePreviewFromLens: async () => undefined,
                copySqlFromLens: async () => undefined,
                openSqlEditorFromLens: async () => undefined,
            },
            getClient: () => ({
                sendRequest: async () => ({ success: true, message: 'Daemon restarted from test.' }),
            }) as never,
            outputChannel: undefined,
            logOutput: message => logs.push(message),
        });

        await import('vscode').then(v => v.commands.executeCommand('efquerylens.restart'));

        const messages = getWindowMessages();
        expect(messages.infos).toContain('EF QueryLens: Daemon restarted from test.');
        expect(logs).toHaveLength(0);

        disposables.forEach(d => d.dispose());
    });

    test('restart command reports incomplete and failure paths', async () => {
        const logs: string[] = [];
        let throwOnRequest = false;

        const disposables = registerQueryLensCommands({
            getSettings: () => ({
                maxCodeLensPerDocument: 50,
                codeLensDebounceMs: 250,
                codeLensUseModelFilter: false,
                formatSqlOnShow: true,
                sqlDialect: 'auto',
                debugLogsEnabled: false,
            }),
            sqlActions: {
                showSqlPopupFromLens: async () => undefined,
                recalculatePreviewFromLens: async () => undefined,
                copySqlFromLens: async () => undefined,
                openSqlEditorFromLens: async () => undefined,
            },
            getClient: () => ({
                sendRequest: async () => {
                    if (throwOnRequest) {
                        throw new Error('restart boom');
                    }

                    return { success: false };
                },
            }) as never,
            outputChannel: undefined,
            logOutput: message => logs.push(message),
        });

        await import('vscode').then(v => v.commands.executeCommand('efquerylens.restart'));
        throwOnRequest = true;
        await import('vscode').then(v => v.commands.executeCommand('efquerylens.restart'));

        const messages = getWindowMessages();
        expect(messages.warnings.some(m => m.includes('QL1007_DAEMON_RESTART_INCOMPLETE'))).toBe(true);
        expect(messages.errors.some(m => m.includes('QL1006_DAEMON_RESTART_FAILED'))).toBe(true);
        expect(logs.some(m => m.includes('QL1007_DAEMON_RESTART_INCOMPLETE'))).toBe(true);
        expect(logs.some(m => m.includes('QL1006_DAEMON_RESTART_FAILED'))).toBe(true);

        disposables.forEach(d => d.dispose());
    });

    test('openOutput command shows output channel', async () => {
        let shown = false;
        const outputChannel = {
            show: () => {
                shown = true;
            },
        } as never;

        const disposables = registerQueryLensCommands({
            getSettings: () => ({
                maxCodeLensPerDocument: 50,
                codeLensDebounceMs: 250,
                codeLensUseModelFilter: false,
                formatSqlOnShow: true,
                sqlDialect: 'auto',
                debugLogsEnabled: false,
            }),
            sqlActions: {
                showSqlPopupFromLens: async () => undefined,
                recalculatePreviewFromLens: async () => undefined,
                copySqlFromLens: async () => undefined,
                openSqlEditorFromLens: async () => undefined,
            },
            getClient: () => undefined,
            outputChannel,
            logOutput: () => undefined,
        });

        await import('vscode').then(v => v.commands.executeCommand('efquerylens.openOutput'));

        expect(shown).toBe(true);

        disposables.forEach(d => d.dispose());
    });

    test('debug logging records command invocation metadata', async () => {
        const logs: string[] = [];

        const disposables = registerQueryLensCommands({
            getSettings: () => ({
                maxCodeLensPerDocument: 50,
                codeLensDebounceMs: 250,
                codeLensUseModelFilter: false,
                formatSqlOnShow: true,
                sqlDialect: 'auto',
                debugLogsEnabled: true,
            }),
            sqlActions: {
                showSqlPopupFromLens: async () => undefined,
                recalculatePreviewFromLens: async () => undefined,
                copySqlFromLens: async () => undefined,
                openSqlEditorFromLens: async () => undefined,
            },
            getClient: () => undefined,
            outputChannel: undefined,
            logOutput: message => logs.push(message),
        });

        await import('vscode').then(v => v.commands.executeCommand('efquerylens.copySql', 'file:///c:/repo/query.cs', 10, 5));

        expect(logs.some(m => m.includes('command copySql'))).toBe(true);
        expect(logs.some(m => m.includes('uriType=string'))).toBe(true);

        disposables.forEach(d => d.dispose());
    });

    test('generateFactory command creates file and opens document on success', async () => {
        setNextSaveDialogUri(Uri.parse('file:///c:/repo/QueryLensDbContextFactory.cs'));

        const disposables = registerQueryLensCommands({
            getSettings: () => ({
                maxCodeLensPerDocument: 50,
                codeLensDebounceMs: 250,
                codeLensUseModelFilter: false,
                formatSqlOnShow: true,
                sqlDialect: 'auto',
                debugLogsEnabled: false,
            }),
            sqlActions: {
                showSqlPopupFromLens: async () => undefined,
                recalculatePreviewFromLens: async () => undefined,
                copySqlFromLens: async () => undefined,
                openSqlEditorFromLens: async () => undefined,
            },
            getClient: () => ({
                sendRequest: async () => ({
                    success: true,
                    content: 'public class QueryLensDbContextFactory {}',
                    suggestedFileName: 'QueryLensDbContextFactory.cs',
                }),
            }) as never,
            outputChannel: undefined,
            logOutput: () => undefined,
        });

        await import('vscode').then(v => v.commands.executeCommand('efquerylens.generateFactory', 'C:/repo/MyApp.dll', 'My.Namespace.MyDbContext'));

        const interactions = getDocumentInteractions();
        expect(interactions.createdFiles.length).toBe(1);
        expect(interactions.createdEdits.length).toBe(1);
        expect(interactions.createdEdits[0].text).toContain('QueryLensDbContextFactory');
        expect(interactions.shownDocuments.length).toBe(1);

        const messages = getWindowMessages();
        expect(messages.infos.some(m => m.includes('Factory file created'))).toBe(true);

        disposables.forEach(d => d.dispose());
    });

    test('generateFactory command handles invalid and failed requests', async () => {
        const disposables = registerQueryLensCommands({
            getSettings: () => ({
                maxCodeLensPerDocument: 50,
                codeLensDebounceMs: 250,
                codeLensUseModelFilter: false,
                formatSqlOnShow: true,
                sqlDialect: 'auto',
                debugLogsEnabled: false,
            }),
            sqlActions: {
                showSqlPopupFromLens: async () => undefined,
                recalculatePreviewFromLens: async () => undefined,
                copySqlFromLens: async () => undefined,
                openSqlEditorFromLens: async () => undefined,
            },
            getClient: () => ({
                sendRequest: async () => ({
                    success: false,
                    message: 'generation failed',
                }),
            }) as never,
            outputChannel: undefined,
            logOutput: () => undefined,
        });

        await import('vscode').then(v => v.commands.executeCommand('efquerylens.generateFactory', undefined, 'My.Namespace.MyDbContext'));
        await import('vscode').then(v => v.commands.executeCommand('efquerylens.generateFactory', 'C:/repo/MyApp.dll', 'My.Namespace.MyDbContext'));

        const messages = getWindowMessages();
        expect(messages.warnings.some(m => m.includes('assembly path is unknown'))).toBe(true);
        expect(messages.errors.some(m => m.includes('generation failed'))).toBe(true);

        disposables.forEach(d => d.dispose());
    });
});
