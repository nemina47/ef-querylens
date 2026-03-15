import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const pluginRoot = path.resolve(__dirname, '..');
const repoRoot = path.resolve(pluginRoot, '..', '..', '..');
const lspOutputRoot = path.join(pluginRoot, 'server');
const daemonOutputRoot = path.join(pluginRoot, 'daemon');

const lspSource = resolveRuntimeDirectory(
  'EFQueryLens.Lsp',
  'EFQueryLens.Lsp.dll',
  repoRoot
);
const daemonSource = resolveRuntimeDirectory(
  'EFQueryLens.Daemon',
  'EFQueryLens.Daemon.dll',
  repoRoot
);

const lspDestination = lspOutputRoot;
const daemonDestination = daemonOutputRoot;

copyDirectory(lspSource, lspDestination);
copyDirectory(daemonSource, daemonDestination);

console.log(`[EFQueryLens] bundled runtime prepared:`);
console.log(`  lsp:    ${lspSource} -> ${lspDestination}`);
console.log(`  daemon: ${daemonSource} -> ${daemonDestination}`);

function resolveRuntimeDirectory(projectName, requiredFileName, repositoryRoot) {
  const debugDir = path.join(repositoryRoot, 'src', projectName, 'bin', 'Debug', 'net10.0');
  const releaseDir = path.join(repositoryRoot, 'src', projectName, 'bin', 'Release', 'net10.0');

  if (fs.existsSync(path.join(debugDir, requiredFileName))) {
    return debugDir;
  }

  if (fs.existsSync(path.join(releaseDir, requiredFileName))) {
    return releaseDir;
  }

  throw new Error(
    `[EFQueryLens] Could not find ${requiredFileName} for ${projectName}. Build ${projectName} first (Debug or Release, net10.0).`
  );
}

function copyDirectory(sourceDir, destinationDir) {
  fs.mkdirSync(path.dirname(destinationDir), { recursive: true });
  fs.rmSync(destinationDir, { recursive: true, force: true });
  fs.cpSync(sourceDir, destinationDir, { recursive: true, force: true });
}
