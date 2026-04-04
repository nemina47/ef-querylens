#!/usr/bin/env node
"use strict";

// TSP Copilot — SonarQube headless setup
// Spins up SonarQube Community via Docker, creates a project + token,
// and writes config to .env.copilot. Run once per project.

const { execSync } = require("node:child_process");
const crypto = require("node:crypto");
const fs = require("node:fs");
const http = require("node:http");
const os = require("node:os");
const path = require("node:path");

const SQ_PORT = 9000;
const SQ_URL = `http://localhost:${SQ_PORT}`;
const CONTAINER = "tsp-sonarqube";
const DEFAULT_USER = "admin";
const DEFAULT_PASS = "admin";

// --- Helpers ---

function run(cmd) {
  return execSync(cmd, { encoding: "utf8", stdio: ["pipe", "pipe", "pipe"] }).trim();
}

function tryRun(cmd) {
  try { return run(cmd); } catch { return null; }
}

function log(msg) { process.stdout.write(`${msg}\n`); }
function warn(msg) { process.stderr.write(`⚠ ${msg}\n`); }
function fail(msg) { process.stderr.write(`✗ ${msg}\n`); process.exit(1); }

function httpRequest(method, urlPath, body, auth) {
  return new Promise((resolve, reject) => {
    const url = new URL(urlPath, SQ_URL);
    const headers = { "Content-Type": "application/x-www-form-urlencoded" };
    if (auth) {
      headers["Authorization"] = `Basic ${Buffer.from(auth).toString("base64")}`;
    }
    const payload = body ? new URLSearchParams(body).toString() : "";
    const req = http.request(
      { hostname: url.hostname, port: url.port, path: url.pathname + url.search, method, headers },
      (res) => {
        let data = "";
        res.on("data", (c) => { data += c; });
        res.on("end", () => {
          const parsed = data ? JSON.parse(data) : {};
          resolve({ status: res.statusCode, body: parsed });
        });
      }
    );
    req.on("error", reject);
    if (payload) req.write(payload);
    req.end();
  });
}

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

// --- Credentials ---

function credentialsPath() {
  return path.join(os.homedir(), ".config", "tsp-copilot", "sq-credentials.json");
}

function loadCredentials(url) {
  const p = credentialsPath();
  if (!fs.existsSync(p)) return null;
  try {
    const data = JSON.parse(fs.readFileSync(p, "utf8"));
    return data[url] || null;
  } catch { return null; }
}

function saveCredentials(url, user, pass) {
  const p = credentialsPath();
  const dir = path.dirname(p);
  if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
  let data = {};
  if (fs.existsSync(p)) {
    try { data = JSON.parse(fs.readFileSync(p, "utf8")); } catch { /* overwrite */ }
  }
  data[url] = { user, pass };
  fs.writeFileSync(p, JSON.stringify(data, null, 2) + "\n", { mode: 0o600 });
}

function generatePassword() {
  // SQ requires: 12+ chars, 1 upper, 1 lower, 1 number, 1 special
  return crypto.randomBytes(8).toString("hex") + "Aa1!";
}

// --- Docker ---

function ensureDocker() {
  if (!tryRun("docker info")) {
    fail("Docker is not running. Start Docker Desktop or the Docker daemon.");
  }
}

async function ensureContainer() {
  const ps = tryRun(`docker ps -a --filter name=^${CONTAINER}$ --format "{{.Status}}"`);
  if (ps?.startsWith("Up")) {
    log(`✓ Container '${CONTAINER}' already running`);
    return;
  }
  if (ps) {
    log(`Starting existing container '${CONTAINER}'...`);
    run(`docker start ${CONTAINER}`);
  } else {
    log(`Creating container '${CONTAINER}' (sonarqube:community)...`);
    run(
      `docker run -d --name ${CONTAINER} -p ${SQ_PORT}:9000 sonarqube:community`
    );
  }
}

async function waitForReady(timeoutMs = 180_000) {
  log("Waiting for SonarQube to start...");
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await httpRequest("GET", "/api/system/status");
      if (res.body.status === "UP") {
        log("✓ SonarQube is ready");
        return;
      }
    } catch { /* not ready yet */ }
    await sleep(3000);
  }
  fail(`SonarQube did not start within ${timeoutMs / 1000}s. Check: docker logs ${CONTAINER}`);
}

// --- SQ API ---

async function changeDefaultPassword(newPass, auth) {
  const res = await httpRequest("POST", "/api/users/change_password", {
    login: DEFAULT_USER,
    previousPassword: DEFAULT_PASS,
    password: newPass,
  }, auth);
  // 204 = success, 400 = already changed (reuse existing password flow)
  return res.status === 204;
}

async function createProject(key, name, auth) {
  const res = await httpRequest("POST", "/api/projects/create", {
    project: key,
    name: name,
  }, auth);
  if (res.status === 200) return true;
  if (res.status === 400 && res.body.errors?.[0]?.msg?.includes("already exists")) return false;
  fail(`Failed to create project: ${JSON.stringify(res.body)}`);
}

async function generateToken(auth, projectKey) {
  const tokenName = `tsp-copilot-${projectKey}`;
  // Delete any existing token with same name first (idempotent)
  await httpRequest("POST", "/api/user_tokens/revoke", { name: tokenName }, auth);
  const res = await httpRequest("POST", "/api/user_tokens/generate", {
    name: tokenName,
    type: "USER_TOKEN",
  }, auth);
  if (res.status === 200 && res.body.token) return res.body.token;
  fail(`Failed to generate token: ${JSON.stringify(res.body)}`);
}

// --- Quality Gate ---

const GATE_NAME = "tsp-strict";

const NEW_CODE_CONDITIONS = [
  { metric: "new_security_hotspots_reviewed", op: "LT", error: "100" },
  { metric: "new_coverage", op: "LT", error: "80" },
  { metric: "new_duplicated_lines_density", op: "GT", error: "3" },
  { metric: "new_duplicated_blocks", op: "GT", error: "0" },
  { metric: "new_maintainability_rating", op: "GT", error: "1" },
  { metric: "new_blocker_violations", op: "GT", error: "0" },
  { metric: "new_critical_violations", op: "GT", error: "0" },
  { metric: "new_major_violations", op: "GT", error: "0" },
  { metric: "new_reliability_rating", op: "GT", error: "1" },
  { metric: "new_security_rating", op: "GT", error: "1" },
];

const OVERALL_CODE_CONDITIONS = [
  { metric: "blocker_violations", op: "GT", error: "0" },
  { metric: "coverage", op: "LT", error: "80" },
  { metric: "critical_violations", op: "GT", error: "0" },
  { metric: "duplicated_blocks", op: "GT", error: "0" },
  { metric: "sqale_rating", op: "GT", error: "1" },
  { metric: "major_violations", op: "GT", error: "0" },
  { metric: "reliability_rating", op: "GT", error: "1" },
  { metric: "security_rating", op: "GT", error: "1" },
  { metric: "skipped_tests", op: "GT", error: "0" },
  { metric: "test_errors", op: "GT", error: "0" },
  { metric: "test_failures", op: "GT", error: "0" },
];

async function configureQualityGate(auth, projectKey) {
  // Find or create the gate
  const createRes = await httpRequest("POST", "/api/qualitygates/create", { name: GATE_NAME }, auth);
  if (createRes.status !== 200) {
    // Already exists — find it and clear conditions for idempotency
    const listRes = await httpRequest("GET", "/api/qualitygates/list", null, auth);
    const gate = listRes.body.qualitygates?.find((g) => g.name === GATE_NAME);
    if (!gate) fail(`Could not create or find quality gate '${GATE_NAME}'.`);
    const showRes = await httpRequest("GET", `/api/qualitygates/show?name=${GATE_NAME}`, null, auth);
    for (const cond of showRes.body.conditions || []) {
      await httpRequest("POST", "/api/qualitygates/delete_condition", { id: String(cond.id) }, auth);
    }
  }

  // Add all conditions
  for (const c of NEW_CODE_CONDITIONS) {
    await httpRequest("POST", "/api/qualitygates/create_condition", {
      gateName: GATE_NAME, metric: c.metric, op: c.op, error: c.error,
    }, auth);
  }
  for (const c of OVERALL_CODE_CONDITIONS) {
    await httpRequest("POST", "/api/qualitygates/create_condition", {
      gateName: GATE_NAME, metric: c.metric, op: c.op, error: c.error,
    }, auth);
  }

  // Assign to project
  await httpRequest("POST", "/api/qualitygates/select", {
    gateName: GATE_NAME, projectKey,
  }, auth);
}

// --- .env.copilot ---

function updateEnvCopilot(url, token, projectKey) {
  const envPath = path.join(process.cwd(), ".env.copilot");
  let content = "";
  if (fs.existsSync(envPath)) {
    content = fs.readFileSync(envPath, "utf8");
    // Update existing SQ values
    const replacements = { SQ_URL: url, SQ_TOKEN: token, SQ_PROJECT_KEY: projectKey };
    let updated = false;
    for (const [key, val] of Object.entries(replacements)) {
      const re = new RegExp(`^${key}=.*$`, "m");
      if (re.test(content)) {
        content = content.replace(re, `${key}=${val}`);
        updated = true;
      }
    }
    if (updated) {
      fs.writeFileSync(envPath, content);
      return;
    }
    // Append new section
    if (!content.endsWith("\n")) content += "\n";
  }
  content +=
    "\n# --- SonarQube (code quality analysis) ---\n" +
    `SQ_URL=${url}\n` +
    `SQ_TOKEN=${token}\n` +
    `SQ_PROJECT_KEY=${projectKey}\n`;
  fs.writeFileSync(envPath, content);
}

function checkGitignore() {
  const gitignorePath = path.join(process.cwd(), ".gitignore");
  if (!fs.existsSync(gitignorePath)) return false;
  const content = fs.readFileSync(gitignorePath, "utf8");
  return /^\.env\.copilot$/m.test(content);
}

function registerMcpServer() {
  const mcpPath = path.join(process.cwd(), ".vscode", "mcp.json");
  let config = { servers: {} };
  if (fs.existsSync(mcpPath)) {
    config = JSON.parse(fs.readFileSync(mcpPath, "utf8"));
    if (!config.servers) config.servers = {};
  }
  if (config.servers["tsp-sonarqube"]) return false;
  config.servers["tsp-sonarqube"] = {
    type: "stdio",
    command: "node",
    args: [".github/scripts/tsp-start-sonarqube-mcp.js"],
  };
  const dir = path.dirname(mcpPath);
  if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(mcpPath, JSON.stringify(config, null, 2) + "\n");
  return true;
}

// --- Main ---

async function main() {
  const projectKey = process.argv[2] || path.basename(process.cwd()).toLowerCase().replaceAll(/[^a-z0-9-]/g, "-");
  const projectName = path.basename(process.cwd());

  log(`\nSonarQube Setup for '${projectName}'\n`);

  // Prerequisites
  ensureDocker();

  // Container
  await ensureContainer();
  await waitForReady();

  // Auth — reuse stored credentials or change default password on first run
  let auth;
  const stored = loadCredentials(SQ_URL);
  if (stored) {
    auth = `${stored.user}:${stored.pass}`;
    const check = await httpRequest("GET", "/api/system/status", null, auth);
    if (check.status === 401) {
      fail(
        "Stored credentials are invalid.\n" +
        `Update ${credentialsPath()} with your current SonarQube password.`
      );
    }
    log("✓ Authenticated with stored credentials");
  } else {
    const defaultAuth = `${DEFAULT_USER}:${DEFAULT_PASS}`;
    const newPass = generatePassword();
    const changed = await changeDefaultPassword(newPass, defaultAuth);
    if (changed) {
      auth = `${DEFAULT_USER}:${newPass}`;
      saveCredentials(SQ_URL, DEFAULT_USER, newPass);
      log(`✓ Default password changed (credentials saved to ${credentialsPath()})`);
    } else {
      // Password already changed but no stored credentials
      fail(
        "Cannot authenticate — default password was already changed.\n" +
        `Create ${credentialsPath()} with your credentials:\n` +
        `  { "${SQ_URL}": { "user": "admin", "pass": "YOUR_PASSWORD" } }`
      );
    }
  }

  // Project
  const created = await createProject(projectKey, projectName, auth);
  log(created ? `✓ Project '${projectKey}' created` : `✓ Project '${projectKey}' already exists`);

  // Quality gate
  await configureQualityGate(auth, projectKey);
  log(`✓ Quality gate '${GATE_NAME}' configured and assigned`);

  // Token (project-scoped so multiple projects don't clobber each other)
  const token = await generateToken(auth, projectKey);
  log("✓ API token generated");

  // Write config
  updateEnvCopilot(SQ_URL, token, projectKey);
  log("✓ Config written to .env.copilot");

  // Register MCP server
  const mcpAdded = registerMcpServer();
  log(mcpAdded ? "✓ MCP server added to .vscode/mcp.json" : "✓ MCP server already in .vscode/mcp.json");

  if (!checkGitignore()) {
    warn("Add .env.copilot to .gitignore — it contains credentials.");
  }

  log(`\n✓ Setup complete!`);
  log(`  SonarQube: ${SQ_URL}`);
  log(`  Project:   ${projectKey}`);
  log(`  Dashboard: ${SQ_URL}/dashboard?id=${projectKey}`);
  log(`\nRun a scan: node .github/scripts/tsp-run-sonar-scan.js\n`);
}

try {
  await main();
} catch (err) {
  process.stderr.write(`\n✗ Setup failed: ${err.message}\n`);
  process.exit(1);
}
