import * as fs from 'fs';
import * as path from 'path';
import {
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

let client: LanguageClient | undefined;
let queryLensOutputChannel: OutputChannel | undefined;

export function activate(context: ExtensionContext) {
    queryLensOutputChannel = window.createOutputChannel('EF QueryLens');
    context.subscriptions.push(queryLensOutputChannel);

    const lspBuildDir = context.asAbsolutePath(
        path.join('..', '..', 'EFQueryLens.Lsp', 'bin', 'Debug', 'net10.0')
    );
    const daemonBuildDir = context.asAbsolutePath(
        path.join('..', '..', 'EFQueryLens.Daemon', 'bin', 'Debug', 'net10.0')
    );

    const stagedRuntime = stageRuntimeBinaries(lspBuildDir, daemonBuildDir);

    const serverPath = stagedRuntime?.lspDllPath
        ?? path.join(lspBuildDir, 'EFQueryLens.Lsp.dll');
    const fallbackRepoRoot = path.resolve(context.extensionPath, '..', '..', '..');
    const workspaceRoot = workspace.workspaceFolders?.[0]?.uri.fsPath ?? fallbackRepoRoot;
    const daemonDllPath = stagedRuntime?.daemonDllPath
        ?? path.join(daemonBuildDir, 'EFQueryLens.Daemon.dll');
    const daemonExePath = stagedRuntime?.daemonExePath
        ?? path.join(daemonBuildDir, 'EFQueryLens.Daemon.exe');

    const settings = readSettings();
    logOutput(`activate workspace=${workspaceRoot}`);
    if (stagedRuntime) {
        logOutput(`[EFQueryLens] runtime staging root=${stagedRuntime.stagingRoot}`);
    } else {
        logOutput('[EFQueryLens] runtime staging unavailable; using build output paths directly');
    }

    const serverEnv: NodeJS.ProcessEnv = {
        ...process.env,
        QUERYLENS_WORKSPACE: workspaceRoot,
        QUERYLENS_DAEMON_WORKSPACE: workspaceRoot,
        QUERYLENS_DAEMON_START_TIMEOUT_MS: '30000',
        QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS: '10000',
        QUERYLENS_DAEMON_SHUTDOWN_ON_DISPOSE: '1',
        QUERYLENS_MAX_CODELENS_PER_DOCUMENT: String(settings.maxCodeLensPerDocument),
        QUERYLENS_CODELENS_DEBOUNCE_MS: String(settings.codeLensDebounceMs),
        QUERYLENS_CODELENS_USE_MODEL_FILTER: settings.codeLensUseModelFilter ? '1' : '0',
        QUERYLENS_DEBUG: settings.debugLogsEnabled ? '1' : '0',
    };

    if (fs.existsSync(daemonExePath)) {
        serverEnv.QUERYLENS_DAEMON_EXE = daemonExePath;
    } else if (settings.debugLogsEnabled) {
        logOutput(`[EFQueryLens] daemon exe not found at ${daemonExePath}`);
    }

    if (fs.existsSync(daemonDllPath)) {
        serverEnv.QUERYLENS_DAEMON_DLL = daemonDllPath;
    } else if (settings.debugLogsEnabled) {
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
            provideHover: async (document, position, token, next) => {
                const hover = await next(document, position, token);
                return enableTrustedHoverCommands(hover as Hover | null, ['efquerylens.copySql', 'efquerylens.showSql', 'efquerylens.openSqlEditor']);
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
        settings,
        sqlActions,
        getClient: () => client,
        outputChannel: queryLensOutputChannel,
        logOutput,
    });
    context.subscriptions.push(...commandDisposables);

    const warmupManager = createWarmupManager({
        getClient: () => client,
        debugLogsEnabled: settings.debugLogsEnabled,
        logOutput,
    });
    context.subscriptions.push(
        window.onDidChangeActiveTextEditor(editor => warmupManager.scheduleWarmup(editor))
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
