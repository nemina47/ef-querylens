import { beforeEach, describe, expect, test, vi } from 'vitest';

import {
    fireDidChangeConfiguration,
    getExecutedCommands,
    getOutputLines,
    getWindowMessages,
    resetVscodeMocks,
    setConfigurationReader,
    setNextInfoChoice,
    workspace,
} from './mocks/vscode';
import { getLanguageClientInstances, resetLanguageClientMocks, State } from './mocks/vscode-languageclient-node';

const fsMock = vi.hoisted(() => ({
    existsSync: vi.fn<(target: unknown) => boolean>(),
}));

vi.mock('fs', () => ({
    existsSync: fsMock.existsSync,
}));

describe('extension activation', () => {
    let settingsState: {
        debounceMs: number;
        useModelFilter: boolean;
        debugEnabled: boolean;
        formatOnShow: boolean;
        sqlDialect: 'auto' | 'sql' | 'mysql' | 'transactsql';
    };

    beforeEach(async () => {
        vi.restoreAllMocks();
        fsMock.existsSync.mockReset();
        resetVscodeMocks();
        resetLanguageClientMocks();
        workspace.workspaceFolders = undefined;
        settingsState = {
            debounceMs: 250,
            useModelFilter: false,
            debugEnabled: false,
            formatOnShow: true,
            sqlDialect: 'auto',
        };

        setConfigurationReader(<T,>(key: string, defaultValue: T) => {
            switch (key) {
                case 'codeLens.maxPerDocument':
                    return 50 as T;
                case 'codeLens.debounceMs':
                    return settingsState.debounceMs as T;
                case 'codeLens.useModelFilter':
                    return settingsState.useModelFilter as T;
                case 'sql.formatOnShow':
                    return settingsState.formatOnShow as T;
                case 'sql.dialect':
                    return settingsState.sqlDialect as T;
                case 'debug.logs.enabled':
                    return settingsState.debugEnabled as T;
                default:
                    return defaultValue;
            }
        });
    });

    test('deactivate returns undefined when not activated', async () => {
        const extension = await import('../src/extension');
        const result = extension.deactivate();
        expect(result).toBeUndefined();
    });

    test('shows runtime missing error and exits early when packaged runtime is absent', async () => {
        fsMock.existsSync.mockReturnValue(false);

        const extension = await import('../src/extension');
        const context = {
            extensionPath: 'c:/repo/src/Plugins/ef-querylens-vscode',
            asAbsolutePath: (relative: string) => `c:/repo/src/Plugins/ef-querylens-vscode/${relative}`,
            subscriptions: [],
        } as never;

        extension.activate(context);

        const messages = getWindowMessages();
        expect(messages.errors.some(m => m.includes('runtime is missing'))).toBe(true);
        expect(getOutputLines().some(line => line.includes('runtime is missing'))).toBe(true);
        expect(getLanguageClientInstances()).toHaveLength(0);

        await extension.deactivate();
    });

    test('starts language client when packaged runtime exists and stops on deactivate', async () => {
        fsMock.existsSync.mockImplementation(target => {
            const value = String(target).replace(/\\/g, '/');
            return value.endsWith('/server/EFQueryLens.Lsp.dll')
                || value.endsWith('/daemon/EFQueryLens.Daemon.dll')
                || value.endsWith('/daemon/EFQueryLens.Daemon.exe');
        });

        const extension = await import('../src/extension');
        workspace.workspaceFolders = [{ uri: { fsPath: 'c:/repo' } }];

        const context = {
            extensionPath: 'c:/repo/src/Plugins/ef-querylens-vscode',
            asAbsolutePath: (relative: string) => `c:/repo/src/Plugins/ef-querylens-vscode/${relative}`,
            subscriptions: [],
        } as never;

        extension.activate(context);

        const clients = getLanguageClientInstances();
        expect(clients).toHaveLength(1);
        expect(clients[0].state).toBe(State.Running);
        expect(getOutputLines().some(line => line.includes('language-client-started'))).toBe(true);

        await extension.deactivate();

        expect(clients[0].state).toBe(State.Stopped);
    });

    test('configuration change pushes runtime settings without restart command when restart settings are unchanged', async () => {
        fsMock.existsSync.mockImplementation(target => {
            const value = String(target).replace(/\\/g, '/');
            return value.endsWith('/server/EFQueryLens.Lsp.dll')
                || value.endsWith('/daemon/EFQueryLens.Daemon.dll')
                || value.endsWith('/daemon/EFQueryLens.Daemon.exe');
        });

        const extension = await import('../src/extension');
        workspace.workspaceFolders = [{ uri: { fsPath: 'c:/repo' } }];

        const context = {
            extensionPath: 'c:/repo/src/Plugins/ef-querylens-vscode',
            asAbsolutePath: (relative: string) => `c:/repo/src/Plugins/ef-querylens-vscode/${relative}`,
            subscriptions: [],
        } as never;

        extension.activate(context);

        settingsState.debugEnabled = true;
        await fireDidChangeConfiguration('efquerylens');

        const clients = getLanguageClientInstances();
        expect(clients).toHaveLength(1);
        expect(clients[0].notifications.some(n => n.method === 'workspace/didChangeConfiguration')).toBe(true);
        expect(getExecutedCommands().some(c => c.id === 'efquerylens.restart')).toBe(false);

        await extension.deactivate();
    });

    test('configuration change can trigger restart command when restart-affecting setting changes', async () => {
        fsMock.existsSync.mockImplementation(target => {
            const value = String(target).replace(/\\/g, '/');
            return value.endsWith('/server/EFQueryLens.Lsp.dll')
                || value.endsWith('/daemon/EFQueryLens.Daemon.dll')
                || value.endsWith('/daemon/EFQueryLens.Daemon.exe');
        });

        const extension = await import('../src/extension');
        workspace.workspaceFolders = [{ uri: { fsPath: 'c:/repo' } }];

        const context = {
            extensionPath: 'c:/repo/src/Plugins/ef-querylens-vscode',
            asAbsolutePath: (relative: string) => `c:/repo/src/Plugins/ef-querylens-vscode/${relative}`,
            subscriptions: [],
        } as never;

        extension.activate(context);

        setNextInfoChoice('Restart Now');
        settingsState.debounceMs = 350;
        await fireDidChangeConfiguration('efquerylens');

        expect(getExecutedCommands().some(c => c.id === 'efquerylens.restart')).toBe(true);

        await extension.deactivate();
    });
});
