import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { spawnSync } from 'child_process';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const pluginRoot = path.resolve(__dirname, '..');
const repoRoot = path.resolve(pluginRoot, '..', '..', '..');
const lspOutputRoot = path.join(pluginRoot, 'server');
const daemonOutputRoot = path.join(pluginRoot, 'daemon');
const runtimeIdentifier = normalizeRuntimeIdentifier(process.env.QUERYLENS_RUNTIME_IDENTIFIER);
const runtimeMode = runtimeIdentifier ? `rid:${runtimeIdentifier}` : 'portable';
const buildConfiguration = 'Release';
const targetFramework = 'net10.0';

const lspSource = resolveRuntimeDirectory(
  'EFQueryLens.Lsp',
  'EFQueryLens.Lsp.dll',
  repoRoot,
  runtimeIdentifier,
  buildConfiguration,
  targetFramework
);
const daemonSource = resolveRuntimeDirectory(
  'EFQueryLens.Daemon',
  'EFQueryLens.Daemon.dll',
  repoRoot,
  runtimeIdentifier,
  buildConfiguration,
  targetFramework
);

const lspDestination = lspOutputRoot;
const daemonDestination = daemonOutputRoot;

copyDirectory(lspSource, lspDestination);
copyDirectory(daemonSource, daemonDestination);

console.log(`[EFQueryLens] bundled runtime prepared:`);
console.log(`  lsp:    ${lspSource} -> ${lspDestination}`);
console.log(`  daemon: ${daemonSource} -> ${daemonDestination}`);
console.log(`  mode:   ${runtimeMode}`);

function resolveRuntimeDirectory(
  projectName,
  requiredFileName,
  repositoryRoot,
  runtime,
  configuration,
  framework
) {
  const projectPath = path.join(repositoryRoot, 'src', projectName, `${projectName}.csproj`);
  const publishDir = getPublishDirectory(repositoryRoot, projectName, configuration, framework, runtime);

  publishProject(projectPath, runtime, configuration, framework);

  if (fs.existsSync(path.join(publishDir, requiredFileName))) {
    return publishDir;
  }

  throw new Error(
    `[EFQueryLens] Could not find ${requiredFileName} for ${projectName} at ${publishDir}.`
  );
}

function publishProject(projectPath, runtime, configuration, framework) {
  const useAppHost = runtime ? 'true' : 'false';
  const args = [
    'publish',
    projectPath,
    '-c',
    configuration,
    '-f',
    framework,
    '--self-contained',
    'false',
    `/p:UseAppHost=${useAppHost}`,
  ];

  if (runtime) {
    args.push('-r', runtime);
  }

  const result = spawnSync(
    'dotnet',
    args,
    {
      stdio: 'inherit',
      cwd: repoRoot,
    }
  );

  if (result.status !== 0) {
    throw new Error(`[EFQueryLens] dotnet publish failed for ${projectPath} (exit code ${result.status ?? 'unknown'}).`);
  }
}

function getPublishDirectory(repositoryRoot, projectName, configuration, framework, runtime) {
  const base = path.join(
    repositoryRoot,
    'src',
    projectName,
    'bin',
    configuration,
    framework
  );

  if (runtime) {
    return path.join(base, runtime, 'publish');
  }

  return path.join(base, 'publish');
}

function normalizeRuntimeIdentifier(value) {
  if (typeof value !== 'string') {
    return null;
  }

  const trimmed = value.trim();
  if (trimmed.length === 0) return null;

  // VS Code Marketplace target names differ from .NET RIDs.
  // Map is inline (not module-scope) to avoid the temporal dead zone — this
  // function is called at line 13 during module evaluation, before any
  // module-scope `const` declared later in the file would be initialized.
  //
  // VS Code target  →  .NET RID
  //   win32-x64    →  win-x64
  //   win32-arm64  →  win-arm64
  //   darwin-x64   →  osx-x64
  //   darwin-arm64 →  osx-arm64
  //   linux-*      →  linux-* (unchanged)
  const vsCodeTargetToRid = {
    'win32-x64': 'win-x64',
    'win32-arm64': 'win-arm64',
    'darwin-x64': 'osx-x64',
    'darwin-arm64': 'osx-arm64',
  };

  return vsCodeTargetToRid[trimmed] ?? trimmed;
}

function copyDirectory(sourceDir, destinationDir) {
  fs.mkdirSync(path.dirname(destinationDir), { recursive: true });

  try {
    const removed = removeDirectoryWithRetry(destinationDir);
    if (!removed) {
      console.warn(
        `[EFQueryLens] Using lock-tolerant in-place copy for ${destinationDir}.`
      );
      const lockedFiles = copyDirectoryBestEffort(sourceDir, destinationDir);
      failIfCriticalRuntimeFilesRemainLocked(lockedFiles);
      return;
    }

    fs.cpSync(sourceDir, destinationDir, { recursive: true, force: true });
    return;
  } catch (error) {
    if (!isFileLockError(error)) {
      throw error;
    }
  }

  console.warn(
    `[EFQueryLens] Could not fully replace ${destinationDir} because files are locked. Falling back to in-place copy.`
  );
  copyDirectoryBestEffort(sourceDir, destinationDir);
}

function removeDirectoryWithRetry(directoryPath) {
  if (!fs.existsSync(directoryPath)) {
    return true;
  }

  // Phase 1: quick attempt before doing anything disruptive.
  try {
    fs.rmSync(directoryPath, { recursive: true, force: true, maxRetries: 1, retryDelay: 50 });
    return true;
  } catch (error) {
    if (!isFileLockError(error)) throw error;
  }

  // Phase 2: kill QueryLens processes (LSP + daemon) that hold locks, then retry with patience.
  killQueryLensProcesses(directoryPath);

  try {
    // Keep retries intentionally low on Windows — recursive retries on a large tree
    // can appear hung when many files are locked by a live extension host.
    fs.rmSync(directoryPath, { recursive: true, force: true, maxRetries: 2, retryDelay: 100 });
    return true;
  } catch (error) {
    if (!isFileLockError(error)) throw error;

    return false;
  }
}

function copyDirectoryBestEffort(sourceDir, destinationDir) {
  fs.mkdirSync(destinationDir, { recursive: true });
  const lockedFiles = [];
  copyDirectoryEntriesBestEffort(sourceDir, destinationDir, lockedFiles);

  if (lockedFiles.length === 0) {
    return lockedFiles;
  }

  const preview = lockedFiles.slice(0, 5);
  for (const lockedPath of preview) {
    console.warn(`[EFQueryLens] Locked file retained: ${lockedPath}`);
  }

  if (lockedFiles.length > preview.length) {
    console.warn(
      `[EFQueryLens] ${lockedFiles.length - preview.length} more locked files were retained in place.`
    );
  }

  return lockedFiles;
}

function failIfCriticalRuntimeFilesRemainLocked(lockedFiles) {
  if (!Array.isArray(lockedFiles) || lockedFiles.length === 0) {
    return;
  }

  const criticalFileNames = new Set([
    'EFQueryLens.Core.dll',
    'EFQueryLens.Lsp.dll',
    'EFQueryLens.Daemon.dll',
  ]);

  const blockedCritical = lockedFiles.filter(filePath =>
    criticalFileNames.has(path.basename(filePath))
  );

  if (blockedCritical.length === 0) {
    return;
  }

  const details = blockedCritical.map(p => `- ${p}`).join('\n');
  throw new Error(
    `[EFQueryLens] Critical runtime files are still locked after recovery. ` +
    `Restart VS Code / EF QueryLens language server and rerun prepare-runtime.\n${details}`
  );
}

function copyDirectoryEntriesBestEffort(sourceDir, destinationDir, lockedFiles) {
  const entries = fs.readdirSync(sourceDir, { withFileTypes: true });
  for (const entry of entries) {
    const sourcePath = path.join(sourceDir, entry.name);
    const destinationPath = path.join(destinationDir, entry.name);

    if (entry.isDirectory()) {
      fs.mkdirSync(destinationPath, { recursive: true });
      copyDirectoryEntriesBestEffort(sourcePath, destinationPath, lockedFiles);
      continue;
    }

    try {
      fs.copyFileSync(sourcePath, destinationPath);
    } catch (error) {
      if (!isFileLockError(error)) {
        throw error;
      }

      lockedFiles.push(destinationPath);
    }
  }
}

function killQueryLensProcesses(directoryPath) {
  console.warn(
    `[EFQueryLens] Runtime files appear locked at ${directoryPath}. Killing QueryLens processes...`
  );

  if (process.platform === 'win32') {
    // Kill any dotnet process whose command line contains EFQueryLens (covers LSP + daemon
    // running as framework-dependent: "dotnet EFQueryLens.Lsp.dll" / "dotnet EFQueryLens.Daemon.dll").
    // Also kills self-contained exe variants if present.
    const killResult = spawnSync(
      'powershell',
      [
        '-NonInteractive',
        '-NoProfile',
        '-Command',
        `Get-WmiObject Win32_Process | Where-Object { $_.CommandLine -like '*EFQueryLens*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }`,
      ],
      {
        stdio: 'ignore',
        timeout: 15000,
      }
    );

    if (killResult.error) {
      console.warn(
        `[EFQueryLens] Process-kill probe timed out or failed (${killResult.error.code ?? 'unknown'}). Continuing with lock-tolerant copy.`
      );
    }

    return;
  }

  // Unix: pkill by command pattern covers all variants.
  spawnSync('pkill', ['-9', '-f', 'EFQueryLens'], { stdio: 'ignore' });
}

function isFileLockError(error) {
  if (error == null || typeof error !== 'object') {
    return false;
  }

  const candidate = error;
  return candidate.code === 'EPERM' || candidate.code === 'EBUSY' || candidate.code === 'EACCES' || candidate.code === 'ENOTEMPTY';
}
