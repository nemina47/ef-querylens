"use strict";

const { execFileSync } = require("node:child_process");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const SUPPORTED_SCHEMA_VERSION = 1;
const INSIGHTS_SYNC_LOCK_FILE = ".insights-sync.lock";

function runGit(args, options = {}) {
  try {
    return execFileSync("git", args, {
      cwd: options.cwd || process.cwd(),
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      timeout: options.timeout || 15_000,
    }).trim();
  } catch (err) {
    const stderr = (err.stderr || "").toString().trim();
    throw new Error(stderr || `git ${args.join(" ")} failed`);
  }
}

function tryRunGit(args, options = {}) {
  try {
    return runGit(args, options);
  } catch {
    return null;
  }
}

function deriveRepoId(commonDir) {
  const digest = crypto.createHash("sha256").update(commonDir).digest("hex");
  return `repo_${digest.slice(0, 12)}`;
}

function isPlainObject(value) {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function ensureDir(dirPath) {
  if (!fs.existsSync(dirPath)) {
    fs.mkdirSync(dirPath, { recursive: true });
  }
}

function readJsonIfExists(filePath) {
  if (!fs.existsSync(filePath)) return null;
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function writeJson(filePath, value) {
  ensureDir(path.dirname(filePath));
  fs.writeFileSync(filePath, JSON.stringify(value, null, 2) + "\n");
}

function assertSupportedSchema(name, value) {
  const schemaVersion = value?.schemaVersion || SUPPORTED_SCHEMA_VERSION;
  if (schemaVersion > SUPPORTED_SCHEMA_VERSION) {
    throw new Error(`${name} schemaVersion ${schemaVersion} is newer than supported ${SUPPORTED_SCHEMA_VERSION}`);
  }
}

function resolveRepoContext(cwd = process.cwd()) {
  const worktreeRoot = runGit(["rev-parse", "--show-toplevel"], { cwd });
  const commonDirRaw = runGit(["rev-parse", "--git-common-dir"], { cwd });
  const commonDirPath = path.isAbsolute(commonDirRaw)
    ? commonDirRaw
    : path.resolve(worktreeRoot, commonDirRaw);
  const commonDir = fs.realpathSync(commonDirPath);
  const rootPath = path.basename(commonDir) === ".git"
    ? path.dirname(commonDir)
    : worktreeRoot;
  const branch = tryRunGit(["rev-parse", "--abbrev-ref", "HEAD"], { cwd }) || "HEAD";
  const head = tryRunGit(["rev-parse", "HEAD"], { cwd }) || null;
  const originUrl = tryRunGit(["config", "--get", "remote.origin.url"], { cwd }) || null;

  return {
    id: deriveRepoId(commonDir),
    name: path.basename(rootPath),
    rootPath,
    worktreeRoot,
    commonDir,
    originUrl,
    branch,
    head,
  };
}

function getRepoStateDir(repoContext) {
  return path.join(os.homedir(), ".tsp-copilot", "projects", repoContext.id);
}

function getRepoJsonPath(repoContext) {
  return path.join(getRepoStateDir(repoContext), "repo.json");
}

function getInsightsBackendPaths(repoContext) {
  const backendDir = getRepoStateDir(repoContext);
  return {
    backendDir,
    backendInsightsPath: path.join(backendDir, "insights.md"),
    backendSignalsPath: path.join(backendDir, "signals.jsonl"),
    lockPath: path.join(backendDir, INSIGHTS_SYNC_LOCK_FILE),
  };
}

function getDefaultRepoMetadata(repoContext) {
  return {
    schemaVersion: SUPPORTED_SCHEMA_VERSION,
    repo: {
      id: repoContext.id,
      name: repoContext.name,
      commonDir: repoContext.commonDir,
      originUrl: repoContext.originUrl,
    },
  };
}

function mergeRepoMetadata(existing, repoContext, partial = {}) {
  const base = getDefaultRepoMetadata(repoContext);
  const existingRepo = isPlainObject(existing.repo) ? existing.repo : null;
  const partialRepo = isPlainObject(partial.repo) ? partial.repo : null;
  const merged = {
    ...existing,
    ...base,
    schemaVersion: SUPPORTED_SCHEMA_VERSION,
    repo: {
      ...existingRepo,
      ...base.repo,
      ...partialRepo,
    },
  };

  for (const [key, value] of Object.entries(partial)) {
    if (key === "schemaVersion" || key === "repo") continue;
    if (isPlainObject(value) && isPlainObject(existing[key])) {
      merged[key] = { ...existing[key], ...value };
    } else {
      merged[key] = value;
    }
  }

  return merged;
}

function ensureRepoJson(repoContext, partial = {}) {
  const repoJsonPath = getRepoJsonPath(repoContext);
  const existing = readJsonIfExists(repoJsonPath) || {};
  assertSupportedSchema("repo.json", existing);
  const merged = mergeRepoMetadata(existing, repoContext, partial);
  writeJson(repoJsonPath, merged);
  return merged;
}

module.exports = {
  SUPPORTED_SCHEMA_VERSION,
  INSIGHTS_SYNC_LOCK_FILE,
  runGit,
  tryRunGit,
  deriveRepoId,
  ensureDir,
  readJsonIfExists,
  writeJson,
  assertSupportedSchema,
  resolveRepoContext,
  getRepoStateDir,
  getRepoJsonPath,
  getInsightsBackendPaths,
  getDefaultRepoMetadata,
  mergeRepoMetadata,
  ensureRepoJson,
};