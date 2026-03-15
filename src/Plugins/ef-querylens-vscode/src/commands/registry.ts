import {
    commands,
    Disposable,
    OutputChannel,
    window,
} from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';

import { SqlActionHandlers } from './sqlActions';
import { QueryLensSettings } from '../types';

export type QueryLensCommandRegistryOptions = {
    settings: QueryLensSettings;
    sqlActions: SqlActionHandlers;
    getClient: () => LanguageClient | undefined;
    outputChannel: OutputChannel | undefined;
    logOutput: (message: string) => void;
};

export function registerQueryLensCommands(options: QueryLensCommandRegistryOptions): Disposable[] {
    const {
        settings,
        sqlActions,
        getClient,
        outputChannel,
        logOutput,
    } = options;

    const showSqlCommand = commands.registerCommand(
        'efquerylens.showSql',
        async (uriInput: unknown, lineInput: unknown, characterInput: unknown) => {
            if (settings.debugLogsEnabled) {
                logCommandInvocation(logOutput, 'showSql', uriInput, lineInput, characterInput);
            }
            await sqlActions.showSqlPopupFromLens(uriInput, lineInput, characterInput);
        }
    );

    const copySqlCommand = commands.registerCommand(
        'efquerylens.copySql',
        async (uriInput: unknown, lineInput: unknown, characterInput: unknown) => {
            if (settings.debugLogsEnabled) {
                logCommandInvocation(logOutput, 'copySql', uriInput, lineInput, characterInput);
            }
            await sqlActions.copySqlFromLens(
                uriInput,
                lineInput,
                characterInput,
                settings.formatSqlOnShow,
                settings.sqlDialect
            );
        }
    );

    const openSqlEditorCommand = commands.registerCommand(
        'efquerylens.openSqlEditor',
        async (uriInput: unknown, lineInput: unknown, characterInput: unknown) => {
            if (settings.debugLogsEnabled) {
                logCommandInvocation(logOutput, 'openSqlEditor', uriInput, lineInput, characterInput);
            }
            await sqlActions.openSqlEditorFromLens(
                uriInput,
                lineInput,
                characterInput,
                settings.formatSqlOnShow,
                settings.sqlDialect
            );
        }
    );

    const openOutputCommand = commands.registerCommand(
        'efquerylens.openOutput',
        async () => {
            outputChannel?.show(true);
        }
    );

    const restartCommand = commands.registerCommand(
        'efquerylens.restart',
        async () => {
            const client = getClient();
            if (!client) {
                window.showWarningMessage('EF QueryLens: language client is not initialized yet.');
                return;
            }

            try {
                const response = await client.sendRequest('efquerylens/daemon/restart', {});
                const { success, message } = parseDaemonRestartResponse(response);

                if (success) {
                    window.showInformationMessage(`EF QueryLens: ${message}`);
                } else {
                    window.showWarningMessage(`EF QueryLens: ${message}`);
                }
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                window.showErrorMessage(`EF QueryLens: daemon restart failed. ${message}`);
            }
        }
    );

    return [
        showSqlCommand,
        copySqlCommand,
        openSqlEditorCommand,
        openOutputCommand,
        restartCommand,
    ];
}

function logCommandInvocation(
    logOutput: (message: string) => void,
    commandName: string,
    uriInput: unknown,
    lineInput: unknown,
    characterInput: unknown
): void {
    logOutput(
        `[EFQueryLens] command ${commandName} uriType=${typeof uriInput} lineType=${typeof lineInput} charType=${typeof characterInput} uri=${String(uriInput)} line=${String(lineInput)} char=${String(characterInput)}`
    );
}

function parseDaemonRestartResponse(response: unknown): { success: boolean; message: string } {
    const success = !!(response && typeof response === 'object' && (response as { success?: unknown }).success === true);
    const message = response && typeof response === 'object' && typeof (response as { message?: unknown }).message === 'string'
        ? (response as { message: string }).message
        : (success ? 'Daemon restarted.' : 'Daemon restart did not complete.');

    return { success, message };
}
