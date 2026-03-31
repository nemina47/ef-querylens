import * as fs from 'fs';
import * as path from 'path';
import {
    commands,
    ExtensionContext,
    Hover,
    OutputChannel,
    window,
    workspace,
} from 'vscode';

import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
} from 'vscode-languageclient/node';

import { readSettings } from './config/settings';
import {
    enableTrustedHoverCommands,
} from './hover/markdown';
import { notifyNoFactoryFound, registerQueryLensCommands } from './commands/registry';
import { createSqlActionHandlers } from './commands/sqlActions';
import { QueryLensSettings } from './types';

let client: LanguageClient | undefined;
let queryLensOutputChannel: OutputChannel | undefined;

export function activate(context: ExtensionContext) {
    queryLensOutputChannel = window.createOutputChannel('EF QueryLens');
    context.subscriptions.push(queryLensOutputChannel);

    const packagedLspDir = context.asAbsolutePath('server');
    const packagedDaemonDir = context.asAbsolutePath('daemon');

    const hasPackagedRuntime =
        fs.existsSync(path.join(packagedLspDir, 'EFQueryLens.Lsp.dll'))
        && fs.existsSync(path.join(packagedDaemonDir, 'EFQueryLens.Daemon.dll'));

    if (!hasPackagedRuntime) {
        const missingMessage =
            `EF QueryLens runtime is missing from extension package. ` +
            `Expected '${packagedLspDir}' and '${packagedDaemonDir}'.`;
        logOutput(`[EFQueryLens] ${missingMessage}`);
        void window.showErrorMessage(missingMessage);
        return;
    }

    const serverPath = path.join(packagedLspDir, 'EFQueryLens.Lsp.dll');
    const fallbackRepoRoot = path.resolve(context.extensionPath, '..', '..', '..');
    const workspaceRoot = workspace.workspaceFolders?.[0]?.uri.fsPath ?? fallbackRepoRoot;
    const daemonDllPath = path.join(packagedDaemonDir, 'EFQueryLens.Daemon.dll');
    const daemonExecutablePath = [
        path.join(packagedDaemonDir, 'EFQueryLens.Daemon.exe'),
        path.join(packagedDaemonDir, 'EFQueryLens.Daemon'),
    ].find(candidate => fs.existsSync(candidate));

    let currentSettings = readSettings();
    logOutput(`activate workspace=${workspaceRoot}`);
    logOutput(`[EFQueryLens] runtime source=packaged lsp=${packagedLspDir} daemon=${packagedDaemonDir}`);

    const serverEnv: NodeJS.ProcessEnv = {
        ...process.env,
        QUERYLENS_CLIENT: 'vscode',
        QUERYLENS_WORKSPACE: workspaceRoot,
        QUERYLENS_DAEMON_WORKSPACE: workspaceRoot,
        QUERYLENS_DAEMON_START_TIMEOUT_MS: '30000',
        QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS: '10000',
        QUERYLENS_DAEMON_SHUTDOWN_ON_DISPOSE: '1',
        // Keep rolling-window latency at 20 samples by default, but honor explicit env overrides.
        QUERYLENS_AVG_WINDOW_SAMPLES: process.env.QUERYLENS_AVG_WINDOW_SAMPLES ?? '20',
        // VS Code hides inline SQL Preview badges; hover/command actions remain available.
        QUERYLENS_MAX_CODELENS_PER_DOCUMENT: '0',
        // InlayHint SQL Preview labels are used by Rider; disable them for VS Code UX.
        QUERYLENS_MAX_INLAY_HINTS_PER_DOCUMENT: '0',
        QUERYLENS_CODELENS_DEBOUNCE_MS: String(currentSettings.codeLensDebounceMs),
        QUERYLENS_CODELENS_USE_MODEL_FILTER: currentSettings.codeLensUseModelFilter ? '1' : '0',
    };

    if (daemonExecutablePath) {
        serverEnv.QUERYLENS_DAEMON_EXE = daemonExecutablePath;
    } else if (currentSettings.debugLogsEnabled) {
        logOutput(`[EFQueryLens] daemon executable not found in ${packagedDaemonDir}`);
    }

    if (fs.existsSync(daemonDllPath)) {
        serverEnv.QUERYLENS_DAEMON_DLL = daemonDllPath;
    } else if (currentSettings.debugLogsEnabled) {
        logOutput(`[EFQueryLens] daemon dll not found at ${daemonDllPath}`);
    }

    const serverOptions: ServerOptions = {
        command: 'dotnet',
        args: [serverPath],
        options: {
            cwd: workspaceRoot,
            env: serverEnv,
        }
    };

    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'csharp' }],
        initializationOptions: buildLspInitializationOptions(currentSettings),
        outputChannel: queryLensOutputChannel,
        middleware: {
            provideCodeLenses: async (_document, _token, _next) => {
                // VS Code UX choice: use hover + explicit commands, no inline SQL Preview code lenses.
                return [];
            },
            provideHover: async (document, position, token, next) => {
                const hover = await next(document, position, token);
                return enableTrustedHoverCommands(hover as Hover | null, ['efquerylens.copySql', 'efquerylens.showSql', 'efquerylens.openSqlEditor', 'efquerylens.recalculate']);
            }
        },
        synchronize: {
            fileEvents: workspace.createFileSystemWatcher('**/*.cs')
        }
    };

    client = new LanguageClient(
        'efquerylens-lsp',
        'EF QueryLens Language Server',
        serverOptions,
        clientOptions
    );

    const sqlActions = createSqlActionHandlers(
        () => client,
        (assemblyPath, dbContextTypeName) =>
            notifyNoFactoryFound(assemblyPath, dbContextTypeName, () => client)
    );
    const commandDisposables = registerQueryLensCommands({
        getSettings: () => currentSettings,
        sqlActions,
        getClient: () => client,
        outputChannel: queryLensOutputChannel,
        logOutput,
    });
    context.subscriptions.push(...commandDisposables);

    context.subscriptions.push(
        workspace.onDidChangeConfiguration(async event => {
            if (!event.affectsConfiguration('efquerylens')) {
                return;
            }

            const previousSettings = currentSettings;
            currentSettings = readSettings();
            logOutput(
                `[EFQueryLens] settings-updated formatOnShow=${currentSettings.formatSqlOnShow} dialect=${currentSettings.sqlDialect} debug=${currentSettings.debugLogsEnabled}`
            );

            await pushLspRuntimeConfiguration(client, currentSettings);

            if (!requiresLanguageServerRestart(previousSettings, currentSettings)) {
                return;
            }

            const selection = await window.showInformationMessage(
                'EF QueryLens: some setting changes require a language server restart to fully apply.',
                'Restart Now'
            );
            if (selection === 'Restart Now') {
                await commands.executeCommand('efquerylens.restart');
            }
        })
    );

    client.start();
    logOutput('language-client-started');
}

export function deactivate(): Thenable<void> | undefined {
    if (!client) {
        return undefined;
    }
    return client.stop();
}

function logOutput(message: string): void {
    queryLensOutputChannel?.appendLine(message);
}

function requiresLanguageServerRestart(previous: QueryLensSettings, next: QueryLensSettings): boolean {
    return previous.codeLensDebounceMs !== next.codeLensDebounceMs
        || previous.codeLensUseModelFilter !== next.codeLensUseModelFilter;
}

function buildLspInitializationOptions(settings: QueryLensSettings): unknown {
    return {
        queryLens: buildLspRuntimeConfiguration(settings)
    };
}

function buildLspRuntimeConfiguration(settings: QueryLensSettings): Record<string, unknown> {
    return {
        debugEnabled: settings.debugLogsEnabled,
        enableLspHover: true,
        hoverProgressNotify: false,
        hoverProgressDelayMs: 350,
        hoverCacheTtlMs: 15_000,
        hoverCancelGraceMs: 1_500,
        markdownQueueAdaptiveWaitMs: 200,
        structuredQueueAdaptiveWaitMs: 200,
        warmupSuccessTtlMs: 60_000,
        warmupFailureCooldownMs: 5_000,
    };
}

async function pushLspRuntimeConfiguration(
    languageClient: LanguageClient | undefined,
    settings: QueryLensSettings,
): Promise<void> {
    if (!languageClient || !languageClient.isRunning()) {
        return;
    }

    await languageClient.sendNotification('workspace/didChangeConfiguration', {
        settings: {
            queryLens: buildLspRuntimeConfiguration(settings)
        }
    });
}

