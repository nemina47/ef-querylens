import {
    commands,
    Disposable,
    OutputChannel,
    Position,
    Uri,
    window,
    workspace,
    WorkspaceEdit,
    ViewColumn,
} from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';

import { SqlActionHandlers } from './sqlActions';
import { FactoryGenerationResponse, QueryLensSettings } from '../types';
import { formatLogMessage, formatUserMessage } from '../utils/errors';

export type QueryLensCommandRegistryOptions = {
    getSettings: () => QueryLensSettings;
    sqlActions: SqlActionHandlers;
    getClient: () => LanguageClient | undefined;
    outputChannel: OutputChannel | undefined;
    logOutput: (message: string) => void;
};

// Tracks which assembly paths have already prompted the user so we don't spam.
const _noFactoryNotifiedPaths = new Set<string>();

/**
 * Shows a one-time "Generate Factory File" notification when QueryLens fails
 * because no factory was found for the given assembly path / DbContext type.
 * Call this from the hover/structured-hover response handler when the error
 * message indicates a missing factory.
 */
export function notifyNoFactoryFound(
    assemblyPath: string,
    dbContextTypeName: string | null | undefined,
    getClient: () => LanguageClient | undefined
): void {
    if (_noFactoryNotifiedPaths.has(assemblyPath)) {
        return;
    }
    _noFactoryNotifiedPaths.add(assemblyPath);

    const label = dbContextTypeName
        ? dbContextTypeName.split('.').pop() ?? dbContextTypeName
        : 'DbContext';

    window.showWarningMessage(
        `EF QueryLens: No factory found for ${label}. Generate one now?`,
        'Generate File',
        'Dismiss'
    ).then(choice => {
        if (choice === 'Generate File') {
            commands.executeCommand(
                'efquerylens.generateFactory',
                assemblyPath,
                dbContextTypeName ?? undefined
            );
        }
    });
}

export function registerQueryLensCommands(options: QueryLensCommandRegistryOptions): Disposable[] {
    const {
        getSettings,
        sqlActions,
        getClient,
        outputChannel,
        logOutput,
    } = options;

    const showSqlCommand = commands.registerCommand(
        'efquerylens.showSql',
        async (uriInput: unknown, lineInput: unknown, characterInput: unknown) => {
            const settings = getSettings();
            if (settings.debugLogsEnabled) {
                logCommandInvocation(logOutput, 'showSql', uriInput, lineInput, characterInput);
            }
            await sqlActions.showSqlPopupFromLens(uriInput, lineInput, characterInput);
        }
    );

    const recalculateCommand = commands.registerCommand(
        'efquerylens.recalculate',
        async (uriInput: unknown, lineInput: unknown, characterInput: unknown) => {
            const settings = getSettings();
            if (settings.debugLogsEnabled) {
                logCommandInvocation(logOutput, 'recalculate', uriInput, lineInput, characterInput);
            }

            await sqlActions.recalculatePreviewFromLens(uriInput, lineInput, characterInput);
        }
    );

    const copySqlCommand = commands.registerCommand(
        'efquerylens.copySql',
        async (uriInput: unknown, lineInput: unknown, characterInput: unknown) => {
            const settings = getSettings();
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
            const settings = getSettings();
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

    const generateFactoryCommand = commands.registerCommand(
        'efquerylens.generateFactory',
        async (assemblyPathInput: unknown, dbContextTypeNameInput: unknown) => {
            const client = getClient();
            if (!client) {
                window.showWarningMessage(formatUserMessage('QL1005_DAEMON_RESTART_NOT_READY', 'Language client is not initialized yet.'));
                return;
            }

            const assemblyPath = typeof assemblyPathInput === 'string' ? assemblyPathInput : undefined;
            const dbContextTypeName = typeof dbContextTypeNameInput === 'string' ? dbContextTypeNameInput : undefined;

            if (!assemblyPath) {
                window.showWarningMessage('EF QueryLens: Cannot generate factory — assembly path is unknown.');
                return;
            }

            try {
                const response = await client.sendRequest<FactoryGenerationResponse>(
                    'efquerylens/generateFactory',
                    { assemblyPath, dbContextTypeName }
                );

                if (!response?.success || !response.content || !response.suggestedFileName) {
                    const msg = response?.message ?? 'Factory generation failed.';
                    window.showErrorMessage(`EF QueryLens: ${msg}`);
                    return;
                }

                // Ask user where to save (default: next to the assembly).
                const assemblyDir = Uri.file(assemblyPath.replace(/[/\\][^/\\]+$/, ''));
                const defaultUri = Uri.joinPath(assemblyDir, response.suggestedFileName);

                const saveUri = await window.showSaveDialog({
                    defaultUri,
                    filters: { 'C# Source': ['cs'] },
                    title: 'Save QueryLens Factory File',
                });

                if (!saveUri) {
                    return; // User cancelled
                }

                const edit = new WorkspaceEdit();
                edit.createFile(saveUri, { overwrite: true, ignoreIfExists: false });
                edit.insert(saveUri, new Position(0, 0), response.content);
                await workspace.applyEdit(edit);

                const doc = await workspace.openTextDocument(saveUri);
                await window.showTextDocument(doc, { viewColumn: ViewColumn.Beside });
                window.showInformationMessage(
                    `EF QueryLens: Factory file created — rebuild your project to activate it.`
                );
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                window.showErrorMessage(`EF QueryLens: Factory generation failed. ${message}`);
            }
        }
    );

    const restartCommand = commands.registerCommand(
        'efquerylens.restart',
        async () => {
            const client = getClient();
            if (!client) {
                window.showWarningMessage(formatUserMessage('QL1005_DAEMON_RESTART_NOT_READY', 'Language client is not initialized yet.'));
                return;
            }

            try {
                const response = await client.sendRequest('efquerylens/daemon/restart', {});
                const { success, message } = parseDaemonRestartResponse(response);

                if (success) {
                    window.showInformationMessage(`EF QueryLens: ${message}`);
                } else {
                    logOutput(formatLogMessage('QL1007_DAEMON_RESTART_INCOMPLETE', `daemon restart incomplete message=${message}`));
                    window.showWarningMessage(formatUserMessage('QL1007_DAEMON_RESTART_INCOMPLETE', message));
                }
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                logOutput(formatLogMessage('QL1006_DAEMON_RESTART_FAILED', `daemon restart failed reason=${message}`));
                window.showErrorMessage(formatUserMessage('QL1006_DAEMON_RESTART_FAILED', `Daemon restart failed. ${message}`));
            }
        }
    );

    return [
        showSqlCommand,
        recalculateCommand,
        copySqlCommand,
        openSqlEditorCommand,
        openOutputCommand,
        restartCommand,
        generateFactoryCommand,
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
