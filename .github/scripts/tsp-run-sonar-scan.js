#!/usr/bin/env node
"use strict";

// TSP Copilot — SonarQube scan orchestration (language-agnostic)
// Reads .sonarsteps, interpolates .env.copilot values, runs commands sequentially,
// then polls the CE task until complete.
// Exit codes: 0 = success, 1 = CE failed, 2 = config missing, 3 = timeout

const { execSync } = require("node:child_process");
const fs = require("node:fs");
const http = require("node:http");
const https = require("node:https");
const path = require("node:path");

// --- Config ---

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

// --- .sonarsteps ---

function loadSonarSteps() {
  const stepsPath = path.join(process.cwd(), ".sonarsteps");
  if (!fs.existsSync(stepsPath)) {
    process.stderr.write(
      "Error: .sonarsteps file not found.\n" +
      "Create a .sonarsteps file in your project root with one command per line.\n" +
      "Use ${SQ_URL}, ${SQ_TOKEN}, and ${SQ_PROJECT_KEY} as placeholders.\n" +
      "See .github/instructions/ for examples.\n"
    );
    process.exit(2);
  }
  return fs.readFileSync(stepsPath, "utf8")
    .split("\n")
    .map((l) => l.trim())
    .filter((l) => l && !l.startsWith("#"));
}

function interpolate(cmd, vars) {
  return cmd
    .replaceAll("${SQ_URL}", vars.SQ_URL)
    .replaceAll("${SQ_TOKEN}", vars.SQ_TOKEN)
    .replaceAll("${SQ_PROJECT_KEY}", vars.SQ_PROJECT_KEY);
}

// --- Helpers ---

function log(msg) { process.stdout.write(`${msg}\n`); }

function runCmd(cmd) {
  log(`> ${cmd}`);
  execSync(cmd, { stdio: "inherit" });
}

function httpGet(urlPath, baseUrl, token) {
  return new Promise((resolve, reject) => {
    const url = new URL(urlPath, baseUrl);
    const client = url.protocol === "https:" ? https : http;
    const headers = { Authorization: `Bearer ${token}` };
    client.get(
      { hostname: url.hostname, port: url.port, path: url.pathname + url.search, headers },
      (res) => {
        let data = "";
        res.on("data", (c) => { data += c; });
        res.on("end", () => resolve(JSON.parse(data)));
      }
    ).on("error", reject);
  });
}

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

// --- Scanner ---

function extractTaskId(filePath) {
  const match = fs.readFileSync(filePath, "utf8").match(/ceTaskId=(.+)/);
  return match ? match[1].trim() : null;
}

function searchDir(baseDir) {
  if (!fs.existsSync(baseDir)) return null;
  // Direct report-task.txt (sonar-scanner CLI)
  const directReport = path.join(baseDir, "report-task.txt");
  if (fs.existsSync(directReport)) {
    const taskId = extractTaskId(directReport);
    if (taskId) return taskId;
  }
  // Numbered subdirectories (dotnet-sonarscanner)
  for (const dir of fs.readdirSync(baseDir).sort().reverse()) {
    const reportPath = path.join(baseDir, dir, "report-task.txt");
    if (!fs.existsSync(reportPath)) continue;
    const taskId = extractTaskId(reportPath);
    if (taskId) return taskId;
  }
  return null;
}

function findReportTask() {
  // Check .sonarqube/out (dotnet-sonarscanner) and .scannerwork (sonar-scanner CLI)
  const candidates = [
    path.join(process.cwd(), ".sonarqube", "out"),
    path.join(process.cwd(), ".scannerwork"),
  ];
  for (const baseDir of candidates) {
    const taskId = searchDir(baseDir);
    if (taskId) return taskId;
  }
  return null;
}

async function pollCeTask(taskId, baseUrl, token, timeoutMs = 300_000) {
  log(`Waiting for analysis to complete (task: ${taskId})...`);
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const res = await httpGet(`/api/ce/task?id=${taskId}`, baseUrl, token);
    const status = res.task?.status;
    if (status === "SUCCESS") {
      log("✓ Analysis complete");
      return true;
    }
    if (status === "FAILED" || status === "CANCELED") {
      log(`✗ Analysis ${status.toLowerCase()}`);
      return false;
    }
    await sleep(3000);
  }
  return null; // timeout
}

// --- Main ---

async function main() {
  const envVars = loadEnv(process.cwd());
  const SQ_URL = envVars.SQ_URL || process.env.SQ_URL;
  const SQ_TOKEN = envVars.SQ_TOKEN || process.env.SQ_TOKEN;
  const SQ_PROJECT_KEY = envVars.SQ_PROJECT_KEY || process.env.SQ_PROJECT_KEY;

  if (!SQ_URL || !SQ_TOKEN || !SQ_PROJECT_KEY) {
    const missing = [
      !SQ_URL && "SQ_URL",
      !SQ_TOKEN && "SQ_TOKEN",
      !SQ_PROJECT_KEY && "SQ_PROJECT_KEY",
    ].filter(Boolean);
    process.stderr.write(
      `Error: Missing SonarQube config: ${missing.join(", ")}\n` +
      "Run: node .github/scripts/tsp-setup-sonarqube.js\n"
    );
    process.exit(2);
  }

  log(`\nSonarQube Scan — ${SQ_PROJECT_KEY}\n`);

  const steps = loadSonarSteps();

  // Execute each step from .sonarsteps
  for (const raw of steps) {
    const cmd = interpolate(raw, { SQ_URL, SQ_TOKEN, SQ_PROJECT_KEY });
    try {
      runCmd(cmd);
    } catch {
      log(`⚠ Command failed — continuing: ${raw}`);
    }
  }

  // Find CE task ID from report-task.txt
  const taskId = findReportTask();
  if (!taskId) {
    process.stderr.write("Error: Could not find report-task.txt with ceTaskId.\n");
    process.exit(1);
  }

  // Poll until complete
  const result = await pollCeTask(taskId, SQ_URL, SQ_TOKEN);
  if (result === true) {
    log(`\n✓ Results ready: ${SQ_URL}/dashboard?id=${SQ_PROJECT_KEY}\n`);
    process.exit(0);
  } else if (result === false) {
    process.exit(1);
  } else {
    process.stderr.write("Error: Analysis timed out (5 minutes).\n");
    process.exit(3);
  }
}

if (require.main === module) {
  main().catch((err) => {
    process.stderr.write(`\n\u2717 Scan failed: ${err.message}\n`);
    process.exit(1);
  });
}

module.exports = { loadEnv, interpolate, extractTaskId, searchDir, findReportTask };
