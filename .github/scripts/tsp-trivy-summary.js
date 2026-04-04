#!/usr/bin/env node
"use strict";

// TSP Copilot — Trivy JSON → compact markdown summary
// Usage: node tsp-trivy-summary.js [path-to-results.json]
// Exit codes: 0 = clean, 1 = CRITICAL/HIGH vulns or misconfigs, 2 = secrets found

const fs = require("node:fs");

const SEVERITY_ORDER = ["CRITICAL", "HIGH", "MEDIUM", "LOW", "UNKNOWN"];

// --- Collectors: extract normalized findings from Trivy results ---

function collectVulns(results) {
  const vulnMap = new Map();
  for (const result of results) {
    if (!Array.isArray(result.Vulnerabilities)) continue;
    const target = result.Target || "unknown";
    for (const v of result.Vulnerabilities) {
      const key = `${v.VulnerabilityID}|${v.PkgName}`;
      if (vulnMap.has(key)) continue;
      vulnMap.set(key, {
        id: v.VulnerabilityID || "",
        pkg: v.PkgName || "",
        installed: v.InstalledVersion || "",
        fixed: v.FixedVersion || "",
        severity: v.Severity || "UNKNOWN",
        title: v.Title || "",
        target,
      });
    }
  }
  return [...vulnMap.values()];
}

function collectSecrets(results) {
  const secrets = [];
  for (const result of results) {
    if (!Array.isArray(result.Secrets)) continue;
    const target = result.Target || "unknown";
    for (const s of result.Secrets) {
      secrets.push({
        file: target,
        severity: s.Severity || "UNKNOWN",
        title: s.Title || "",
        startLine: s.StartLine || 0,
        match: s.Match || "",
      });
    }
  }
  return secrets;
}

function collectMisconfigs(results) {
  const misconfigs = [];
  for (const result of results) {
    if (!Array.isArray(result.Misconfigurations)) continue;
    const target = result.Target || "unknown";
    for (const m of result.Misconfigurations) {
      if (m.Status !== "FAIL") continue;
      misconfigs.push({
        id: m.ID || "",
        file: target,
        title: m.Title || "",
        message: m.Message || "",
        severity: m.Severity || "UNKNOWN",
        resource: m.CauseMetadata?.Resource || "",
        startLine: m.CauseMetadata?.StartLine || 0,
      });
    }
  }
  return misconfigs;
}

// --- Formatters: build markdown sections ---

function formatHeader(targetCount, vulns, secrets, misconfigs) {
  const vulnCounts = countBySeverity(vulns);
  const breakdown = SEVERITY_ORDER
    .filter((s) => vulnCounts[s] > 0)
    .map((s) => `${vulnCounts[s]} ${s}`)
    .join(", ");
  const vulnPart = breakdown
    ? `${vulns.length} vulnerabilities (${breakdown})`
    : `${vulns.length} vulnerabilities`;
  return [
    "## Security Scan Summary",
    `Scanned ${targetCount} targets | ${vulnPart} | ${secrets.length} secrets | ${misconfigs.length} misconfigurations`,
    "",
  ];
}

function formatVulnTable(vulns) {
  const items = sortBySeverity(filterCritHigh(vulns));
  if (items.length === 0) return [];
  const rows = items.map((v) => {
    const fixed = v.fixed || "No fix";
    return `| ${v.severity} | ${v.pkg} | ${v.installed} | ${fixed} | ${v.id} | ${truncate(v.title, 60)} |`;
  });
  return [
    "### Vulnerabilities (CRITICAL/HIGH)",
    "",
    "| Severity | Package | Installed | Fixed | CVE | Title |",
    "|----------|---------|-----------|-------|-----|-------|",
    ...rows,
    "",
  ];
}

function formatSecretsTable(secrets) {
  if (secrets.length === 0) return [];
  const rows = sortBySeverity(secrets).map(
    (s) => `| ${s.severity} | ${s.file}:${s.startLine} | ${s.title} | ${s.match} |`
  );
  return [
    "### Secrets",
    "",
    "| Severity | File | Rule | Match |",
    "|----------|------|------|-------|",
    ...rows,
    "",
  ];
}

function formatMisconfigTable(misconfigs) {
  const items = sortBySeverity(filterCritHigh(misconfigs));
  if (items.length === 0) return [];
  const rows = items.map((m) => {
    const resource = m.resource ? ` (${m.resource})` : "";
    const loc = m.startLine ? `${m.file}:${m.startLine}` : m.file;
    return `| ${m.severity} | ${loc} | ${m.id} | ${truncate(m.title, 40)}${resource} | ${truncate(m.message, 60)} |`;
  });
  return [
    "### Misconfigurations (CRITICAL/HIGH)",
    "",
    "| Severity | File | Check | Resource | Message |",
    "|----------|------|-------|----------|---------|",
    ...rows,
    "",
  ];
}

function formatMedLow(vulns, misconfigs) {
  const medLowV = vulns.filter((v) => !isCritHigh(v.severity));
  const medLowM = misconfigs.filter((m) => !isCritHigh(m.severity));
  if (medLowV.length === 0 && medLowM.length === 0) return [];
  const parts = [];
  if (medLowV.length > 0) parts.push(`${medLowV.length} vulnerabilities`);
  if (medLowM.length > 0) parts.push(`${medLowM.length} misconfigurations`);
  return [
    `### MEDIUM/LOW: ${parts.join(", ")} (see .trivy-results.json for details)`,
    "",
  ];
}

// --- Helpers ---

function isCritHigh(severity) {
  return severity === "CRITICAL" || severity === "HIGH";
}

function filterCritHigh(items) {
  return items.filter((i) => isCritHigh(i.severity));
}

function countBySeverity(items) {
  const counts = {};
  for (const s of SEVERITY_ORDER) counts[s] = 0;
  for (const item of items) counts[item.severity || "UNKNOWN"]++;
  return counts;
}

function sortBySeverity(items) {
  return [...items].sort(
    (a, b) => SEVERITY_ORDER.indexOf(a.severity) - SEVERITY_ORDER.indexOf(b.severity)
  );
}

function truncate(str, max) {
  if (!str) return "";
  return str.length > max ? str.slice(0, max - 1) + "\u2026" : str;
}

// --- Main ---

function main() {
  const inputPath = process.argv[2] || ".trivy-results.json";
  if (!fs.existsSync(inputPath)) {
    console.error(`File not found: ${inputPath}`);
    process.exit(3);
  }

  const report = JSON.parse(fs.readFileSync(inputPath, "utf8"));
  const results = report.Results || [];

  const vulns = collectVulns(results);
  const secrets = collectSecrets(results);
  const misconfigs = collectMisconfigs(results);

  const lines = [
    ...formatHeader(results.length, vulns, secrets, misconfigs),
    ...formatVulnTable(vulns),
    ...formatSecretsTable(secrets),
    ...formatMisconfigTable(misconfigs),
    ...formatMedLow(vulns, misconfigs),
  ];

  if (vulns.length === 0 && secrets.length === 0 && misconfigs.length === 0) {
    lines.push("No vulnerabilities, secrets, or misconfigurations found.", "");
  }

  process.stdout.write(lines.join("\n"));

  if (secrets.length > 0) process.exit(2);
  if (filterCritHigh(vulns).length > 0 || filterCritHigh(misconfigs).length > 0) process.exit(1);
  process.exit(0);
}

if (require.main === module) {
  try {
    main();
  } catch (err) {
    console.error(`tsp-trivy-summary: ${err.message}`);
    process.exit(3);
  }
}

module.exports = {
  SEVERITY_ORDER,
  collectVulns,
  collectSecrets,
  collectMisconfigs,
  formatHeader,
  formatVulnTable,
  formatSecretsTable,
  formatMisconfigTable,
  formatMedLow,
  isCritHigh,
  filterCritHigh,
  countBySeverity,
  sortBySeverity,
  truncate,
};
