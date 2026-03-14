import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import { StagedRuntime } from '../types';

export function stageRuntimeBinaries(
    lspBuildDir: string,
    daemonBuildDir: string
): StagedRuntime | null {
    try {
        const lspDll = path.join(lspBuildDir, 'EFQueryLens.Lsp.dll');
        const daemonDll = path.join(daemonBuildDir, 'EFQueryLens.Daemon.dll');
        const daemonExe = path.join(daemonBuildDir, 'EFQueryLens.Daemon.exe');

        if (!fs.existsSync(lspDll) || !fs.existsSync(daemonDll)) {
            return null;
        }

        const stagingBaseRoot = path.join(os.tmpdir(), 'EFQueryLens', 'vscode-runtime');
        const stagingRoot = path.join(stagingBaseRoot, `${Date.now()}-${process.pid}`);
        const lspStageDir = path.join(stagingRoot, 'lsp');
        const daemonStageDir = path.join(stagingRoot, 'daemon');

        fs.mkdirSync(lspStageDir, { recursive: true });
        fs.mkdirSync(daemonStageDir, { recursive: true });
        fs.cpSync(lspBuildDir, lspStageDir, { recursive: true, force: true });
        fs.cpSync(daemonBuildDir, daemonStageDir, { recursive: true, force: true });

        cleanupOldStagingRoots(stagingBaseRoot, stagingRoot);

        return {
            lspDllPath: path.join(lspStageDir, 'EFQueryLens.Lsp.dll'),
            daemonDllPath: path.join(daemonStageDir, 'EFQueryLens.Daemon.dll'),
            daemonExePath: path.join(daemonStageDir, 'EFQueryLens.Daemon.exe'),
            stagingRoot,
        };
    } catch {
        return null;
    }
}

function cleanupOldStagingRoots(stagingBaseRoot: string, activeStagingRoot: string): void {
    try {
        const maxEntries = 5;
        const maxAgeMs = 24 * 60 * 60 * 1000;
        const now = Date.now();

        const entries = fs.readdirSync(stagingBaseRoot, { withFileTypes: true })
            .filter(entry => entry.isDirectory())
            .map(entry => {
                const fullPath = path.join(stagingBaseRoot, entry.name);
                const mtimeMs = fs.statSync(fullPath).mtimeMs;
                return { fullPath, mtimeMs };
            })
            .sort((a, b) => b.mtimeMs - a.mtimeMs);

        for (let i = 0; i < entries.length; i++) {
            const entry = entries[i];
            if (entry.fullPath === activeStagingRoot) {
                continue;
            }

            const tooManyEntries = i >= maxEntries;
            const tooOld = now - entry.mtimeMs > maxAgeMs;
            if (tooManyEntries || tooOld) {
                fs.rmSync(entry.fullPath, { recursive: true, force: true });
            }
        }
    } catch {
        // Best-effort cleanup only.
    }
}
