import { TextEditor } from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';

export type WarmupManagerOptions = {
    getClient: () => LanguageClient | undefined;
    debugLogsEnabled: boolean;
    logOutput: (message: string) => void;
};

export type WarmupManager = {
    scheduleWarmup: (editor: TextEditor | undefined) => void;
    requestDaemonRestartOnActivate: () => Promise<void>;
};

export function createWarmupManager(options: WarmupManagerOptions): WarmupManager {
    const { getClient, debugLogsEnabled, logOutput } = options;
    const warmedDocumentVersions = new Map<string, number>();
    const warmupInFlightUris = new Set<string>();

    const scheduleWarmup = (editor: TextEditor | undefined): void => {
        const client = getClient();
        if (!client) {
            return;
        }

        const document = editor?.document;
        if (!document) {
            return;
        }

        if (document.uri.scheme !== 'file' || document.languageId !== 'csharp') {
            return;
        }

        const uri = document.uri.toString();
        const lastWarmedVersion = warmedDocumentVersions.get(uri);
        if (typeof lastWarmedVersion === 'number' && lastWarmedVersion >= document.version) {
            return;
        }

        if (warmupInFlightUris.has(uri)) {
            return;
        }

        const requestedLine = typeof editor?.selection?.active?.line === 'number'
            ? editor.selection.active.line
            : 0;
        const requestedCharacter = typeof editor?.selection?.active?.character === 'number'
            ? editor.selection.active.character
            : 0;
        const line = Math.max(0, Math.floor(requestedLine));
        const character = Math.max(0, Math.floor(requestedCharacter));

        warmupInFlightUris.add(uri);

        void whenClientReady(client).then(async () => {
            try {
                await client.sendRequest('efquerylens/warmup', {
                    textDocument: { uri },
                    position: { line, character },
                });

                warmedDocumentVersions.set(uri, document.version);
                if (debugLogsEnabled) {
                    logOutput(`[EFQueryLens] warmup rpc uri=${uri} line=${line} character=${character}`);
                }
            } catch (error) {
                if (debugLogsEnabled) {
                    const message = error instanceof Error ? error.message : String(error);
                    logOutput(`[EFQueryLens] warmup skipped uri=${uri} reason=${message}`);
                }
            } finally {
                warmupInFlightUris.delete(uri);
            }
        });
    };

    const requestDaemonRestartOnActivate = async (): Promise<void> => {
        const client = getClient();
        if (!client) {
            return;
        }

        await whenClientReady(client);

        try {
            const response = await client.sendRequest('efquerylens/daemon/restart', {});
            const success = !!(response && typeof response === 'object' && (response as { success?: unknown }).success === true);
            const message = response && typeof response === 'object' && typeof (response as { message?: unknown }).message === 'string'
                ? (response as { message: string }).message
                : (success ? 'Daemon restarted.' : 'Daemon restart did not complete.');

            logOutput(`[EFQueryLens] startup-daemon-restart success=${success} message=${message}`);
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            logOutput(`[EFQueryLens] startup-daemon-restart failed reason=${message}`);
        }
    };

    return {
        scheduleWarmup,
        requestDaemonRestartOnActivate,
    };
}

function whenClientReady(client: LanguageClient): Promise<void> {
    const onReady = (client as unknown as { onReady?: () => Promise<void> }).onReady;
    return typeof onReady === 'function'
        ? onReady.call(client)
        : Promise.resolve();
}
