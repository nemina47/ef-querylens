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
import { registerQueryLensCommands } from './commands/registry';
import { createSqlActionHandlers } from './commands/sqlActions';
import { stageRuntimeBinaries } from './runtime/staging';
import { createWarmupManager } from './runtime/warmup';
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

    const stagedRuntime = stageRuntimeBinaries(packagedLspDir, packagedDaemonDir);

    const serverPath = stagedRuntime?.lspDllPath
        ?? path.join(packagedLspDir, 'EFQueryLens.Lsp.dll');
    const fallbackRepoRoot = path.resolve(context.extensionPath, '..', '..', '..');
    const workspaceRoot = workspace.workspaceFolders?.[0]?.uri.fsPath ?? fallbackRepoRoot;
    const daemonDllPath = stagedRuntime?.daemonDllPath
        ?? path.join(packagedDaemonDir, 'EFQueryLens.Daemon.dll');
    const daemonExePath = stagedRuntime?.daemonExePath
        ?? path.join(packagedDaemonDir, 'EFQueryLens.Daemon.exe');

    let currentSettings = readSettings();
    logOutput(`activate workspace=${workspaceRoot}`);
    logOutput(`[EFQueryLens] runtime source=packaged lsp=${packagedLspDir} daemon=${packagedDaemonDir}`);
    if (stagedRuntime) {
        logOutput(`[EFQueryLens] runtime staging root=${stagedRuntime.stagingRoot}`);
    } else {
        logOutput('[EFQueryLens] runtime staging unavailable; using packaged runtime paths directly');
    }

    const serverEnv: NodeJS.ProcessEnv = {
        ...process.env,
        QUERYLENS_CLIENT: 'vscode',
        QUERYLENS_WORKSPACE: workspaceRoot,
        QUERYLENS_DAEMON_WORKSPACE: workspaceRoot,
        QUERYLENS_DAEMON_START_TIMEOUT_MS: '30000',
        QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS: '10000',
        QUERYLENS_DAEMON_SHUTDOWN_ON_DISPOSE: '1',
        // Use a fixed rolling window (last N samples) for status average latency.
        QUERYLENS_AVG_WINDOW_SAMPLES: '20',
        // Keep shared hover computations alive briefly when VS Code rapidly cancels/reissues hover requests.
        QUERYLENS_HOVER_CANCEL_GRACE_MS: '1500',
        // VS Code hides inline SQL Preview badges; hover/command actions remain available.
        QUERYLENS_MAX_CODELENS_PER_DOCUMENT: '0',
        // InlayHint SQL Preview labels are used by Rider; disable them for VS Code UX.
        QUERYLENS_MAX_INLAY_HINTS_PER_DOCUMENT: '0',
        QUERYLENS_CODELENS_DEBOUNCE_MS: String(currentSettings.codeLensDebounceMs),
        QUERYLENS_CODELENS_USE_MODEL_FILTER: currentSettings.codeLensUseModelFilter ? '1' : '0',
        QUERYLENS_DEBUG: currentSettings.debugLogsEnabled ? '1' : '0',
    };

    if (fs.existsSync(daemonExePath)) {
        serverEnv.QUERYLENS_DAEMON_EXE = daemonExePath;
    } else if (currentSettings.debugLogsEnabled) {
        logOutput(`[EFQueryLens] daemon exe not found at ${daemonExePath}`);
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

    const sqlActions = createSqlActionHandlers(() => client);
    const commandDisposables = registerQueryLensCommands({
        getSettings: () => currentSettings,
        sqlActions,
        getClient: () => client,
        outputChannel: queryLensOutputChannel,
        logOutput,
    });
    context.subscriptions.push(...commandDisposables);

    const warmupManager = createWarmupManager({
        getClient: () => client,
        getDebugLogsEnabled: () => currentSettings.debugLogsEnabled,
        logOutput,
    });
    context.subscriptions.push(
        window.onDidChangeActiveTextEditor(editor => warmupManager.scheduleWarmup(editor))
    );
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
    void warmupManager.requestDaemonRestartOnActivate().finally(() => {
        warmupManager.scheduleWarmup(window.activeTextEditor);
    });
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
        || previous.codeLensUseModelFilter !== next.codeLensUseModelFilter
        || previous.debugLogsEnabled !== next.debugLogsEnabled;
}
