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
const runtimeIdentifier = process.env.QUERYLENS_RUNTIME_IDENTIFIER ?? getDefaultRuntimeIdentifier();
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
console.log(`  rid:    ${runtimeIdentifier}`);

function resolveRuntimeDirectory(
  projectName,
  requiredFileName,
  repositoryRoot,
  runtime,
  configuration,
  framework
) {
  const projectPath = path.join(repositoryRoot, 'src', projectName, `${projectName}.csproj`);
  const publishDir = path.join(
    repositoryRoot,
    'src',
    projectName,
    'bin',
    configuration,
    framework,
    runtime,
    'publish'
  );

  publishProject(projectPath, runtime, configuration, framework);

  if (fs.existsSync(path.join(publishDir, requiredFileName))) {
    return publishDir;
  }

  throw new Error(
    `[EFQueryLens] Could not find ${requiredFileName} for ${projectName} at ${publishDir}.`
  );
}

function publishProject(projectPath, runtime, configuration, framework) {
  const result = spawnSync(
    'dotnet',
    [
      'publish',
      projectPath,
      '-c',
      configuration,
      '-f',
      framework,
      '-r',
      runtime,
      '--self-contained',
      'false',
      '/p:UseAppHost=true',
    ],
    {
      stdio: 'inherit',
      cwd: repoRoot,
    }
  );

  if (result.status !== 0) {
    throw new Error(`[EFQueryLens] dotnet publish failed for ${projectPath} (exit code ${result.status ?? 'unknown'}).`);
  }
}

function getDefaultRuntimeIdentifier() {
  if (process.platform === 'win32') {
    return process.arch === 'arm64' ? 'win-arm64' : 'win-x64';
  }

  if (process.platform === 'darwin') {
    return process.arch === 'arm64' ? 'osx-arm64' : 'osx-x64';
  }

  if (process.platform === 'linux') {
    return process.arch === 'arm64' ? 'linux-arm64' : 'linux-x64';
  }

  throw new Error(`[EFQueryLens] Unsupported platform '${process.platform}'. Set QUERYLENS_RUNTIME_IDENTIFIER explicitly.`);
}

function copyDirectory(sourceDir, destinationDir) {
  fs.mkdirSync(path.dirname(destinationDir), { recursive: true });
  fs.rmSync(destinationDir, { recursive: true, force: true });
  fs.cpSync(sourceDir, destinationDir, { recursive: true, force: true });
}
