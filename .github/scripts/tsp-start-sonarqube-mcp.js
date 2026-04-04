#!/usr/bin/env node
"use strict";

// TSP Copilot — SonarQube MCP launcher
// Reads SQ config from .env.copilot, then spawns the official SonarSource MCP server.
// Used as the MCP server command in .vscode/mcp.json.

const { spawn } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");

function loadEnv(dir) {
  const envPath = path.join(dir, ".env.copilot");
  if (!fs.existsSync(envPath)) return {};
  const vars = {};
  for (const line of fs.readFileSync(envPath, "utf8").split("\n")) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) continue;
    const eq = trimmed.indexOf("=");
    if (eq < 1) continue;
    const key = trimmed.slice(0, eq).trim();
    const value = trimmed.slice(eq + 1).trim().replaceAll(/^["']|["']$/g, "");
    vars[key] = value;
  }
  return vars;
}

const env = loadEnv(process.cwd());
const sqUrl = env.SQ_URL || process.env.SQ_URL;
const sqToken = env.SQ_TOKEN || process.env.SQ_TOKEN;
const sqProjectKey = env.SQ_PROJECT_KEY || process.env.SQ_PROJECT_KEY;

if (!sqUrl || !sqToken || !sqProjectKey) {
  const missing = [
    !sqUrl && "SQ_URL",
    !sqToken && "SQ_TOKEN",
    !sqProjectKey && "SQ_PROJECT_KEY",
  ].filter(Boolean);
  process.stderr.write(
    `Error: Missing SonarQube config: ${missing.join(", ")}\n` +
    "Run the setup script:  node .github/scripts/tsp-setup-sonarqube.js\n" +
    "Or add the values to your .env.copilot file. See .env.copilot.example.\n"
  );
  process.exit(2);
}

// macOS Docker Desktop does not support --network host.
// Replace localhost with host.docker.internal so the MCP container can reach SQ.
let dockerUrl = sqUrl;
if (os.platform() === "darwin" && /localhost|127\.0\.0\.1/.test(sqUrl)) {
  dockerUrl = sqUrl.replace(/localhost|127\.0\.0\.1/, "host.docker.internal");
}

const child = spawn("docker", [
  "run", "--rm", "-i",
  ...(os.platform() === "darwin" ? [] : ["--network", "host"]),
  "-e", `SONARQUBE_URL=${dockerUrl}`,
  "-e", `SONARQUBE_TOKEN=${sqToken}`,
  "-e", `SONARQUBE_PROJECT_KEY=${sqProjectKey}`,
  "-e", "SONARQUBE_READ_ONLY=true",
  "mcp/sonarqube",
], {
  stdio: "inherit",
});

child.on("exit", (code) => process.exit(code ?? 1));
