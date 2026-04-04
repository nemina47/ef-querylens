#!/usr/bin/env node
"use strict";

// TSP Copilot — local usage telemetry hook
// Appends agent lifecycle events to ~/.tsp-copilot/logs/YYYY-MM-DD.jsonl
// Runs on: SessionStart, Stop, SubagentStart, SubagentStop
// Never fails — all errors are swallowed silently.

const { execSync } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");

function readHookInput() {
  const raw = fs.readFileSync(0, "utf8");
  if (!raw?.trim()) throw new Error("Hook stdin was empty");
  return JSON.parse(raw);
}

// VS Code sends snake_case; normalize to the names our code uses.
function normalizeInput(input) {
  return {
    timestamp: input.timestamp,
    hookEventName: input.hookEventName || input.hook_event_name,
    sessionId: input.sessionId || input.session_id,
    cwd: input.cwd,
    agent_id: input.agent_id,
    agent_type: input.agent_type,
  };
}

function buildLogEntry(input, gitUser, gitEmail) {
  const entry = {
    ts: input.timestamp || new Date().toISOString(),
    event: input.hookEventName,
    sessionId: input.sessionId,
    project: input.cwd ? path.basename(input.cwd) : undefined,
    gitUser,
    gitEmail,
    cwd: input.cwd,
  };

  // Add subagent-specific fields
  if (input.agent_id) entry.agentId = input.agent_id;
  if (input.agent_type) entry.agentType = input.agent_type;

  return entry;
}

function getLogPath(homeDir) {
  const today = new Date().toISOString().slice(0, 10);
  return path.join(homeDir, ".tsp-copilot", "logs", `${today}.jsonl`);
}

function main() {
  // Check opt-out before doing any work
  const configPath = path.join(os.homedir(), ".tsp-copilot", "config.json");
  if (fs.existsSync(configPath)) {
    const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
    if (config.telemetry === false) {
      process.stdout.write("{}");
      return;
    }
  }

  const input = normalizeInput(readHookInput());

  // Git identity — best-effort, never fatal
  let gitUser = "unknown user";
  let gitEmail = "unknown email";
  try {
    gitUser = execSync("git config user.name", { encoding: "utf8", timeout: 3000 }).trim() || gitUser;
    gitEmail = execSync("git config user.email", { encoding: "utf8", timeout: 3000 }).trim() || gitEmail;
  } catch {
    // git not available or not configured — use defaults
  }

  const entry = buildLogEntry(input, gitUser, gitEmail);

  // Write to daily log file
  const logFile = getLogPath(os.homedir());
  const logDir = path.dirname(logFile);
  fs.mkdirSync(logDir, { recursive: true });
  fs.appendFileSync(logFile, JSON.stringify(entry) + "\n");

  process.stdout.write("{}");
}

if (require.main === module) {
  try {
    main();
  } catch {
    // Telemetry must never break a session
    process.stdout.write("{}");
  }
}

module.exports = { buildLogEntry, getLogPath, normalizeInput, readHookInput };
