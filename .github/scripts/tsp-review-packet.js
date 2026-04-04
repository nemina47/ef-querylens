#!/usr/bin/env node
"use strict";

// TSP Copilot — Review Packet Builder
// Generates a deterministic review context bundle from an impldoc and changed files.
// Usage: node tsp-review-packet.js <impldoc-path> [--diff-from <ref>] [--output <path>]
// Exit codes: 0 = success, 1 = impldoc not found, 2 = invalid arguments

const fs = require("node:fs");
const path = require("node:path");
const { execSync } = require("node:child_process");

const VERSION = "3.0.0";

// --- Argument parsing ---

function parseArgs(argv) {
  const args = { impldocPath: null, diffFrom: null, output: null, quiet: false };
  let i = 2;
  while (i < argv.length) {
    if (argv[i] === "--diff-from" && argv[i + 1]) {
      args.diffFrom = argv[i + 1];
      i += 2;
    } else if (argv[i] === "--output" && argv[i + 1]) {
      args.output = argv[i + 1];
      i += 2;
    } else if (argv[i] === "--quiet") {
      args.quiet = true;
      i++;
    } else if (argv[i].startsWith("--")) {
      console.error(`Unknown option: ${argv[i]}`);
      process.exit(2);
    } else {
      args.impldocPath = argv[i];
      i++;
    }
  }
  if (!args.impldocPath) {
    console.error("Usage: node tsp-review-packet.js <impldoc-path> [--diff-from <ref>] [--output <path>] [--quiet]");
    process.exit(2);
  }
  return args;
}

// --- Git helpers ---

function gitExec(cmd) {
  try {
    return execSync(cmd, { encoding: "utf8", timeout: 30000 }).trim();
  } catch {
    return "";
  }
}

function getChangedFiles(diffFrom) {
  const output = gitExec(`git diff --name-only ${diffFrom}`);
  if (!output) return [];
  return output.split("\n").filter(Boolean);
}

function getStagedFiles() {
  const output = gitExec("git diff --cached --name-only");
  if (!output) return [];
  return output.split("\n").filter(Boolean);
}

function getUntrackedFiles() {
  const output = gitExec("git ls-files --others --exclude-standard");
  if (!output) return [];
  return output.split("\n").filter(Boolean);
}

function getMergeBase() {
  // Try the remote default branch first (works regardless of local branch naming)
  const remoteHead = gitExec("git symbolic-ref refs/remotes/origin/HEAD");
  if (remoteHead) {
    const remoteBranch = remoteHead.replace(/^refs\/remotes\//, "");
    const base = gitExec(`git merge-base HEAD ${remoteBranch}`);
    if (base) return base;
  }
  // Fall back to well-known branch names
  for (const branch of ["main", "master", "develop"]) {
    const base = gitExec(`git merge-base HEAD ${branch}`);
    if (base) return base;
  }
  return null;
}

function resolveDiffFrom(explicit) {
  if (explicit) return explicit;
  const mergeBase = getMergeBase();
  if (mergeBase) return mergeBase;
  return "HEAD~1";
}

function getDiffStat(diffFrom) {
  return gitExec(`git diff --stat ${diffFrom}`);
}

function getCurrentCommit() {
  return gitExec("git rev-parse --short HEAD");
}

function getCurrentBranch() {
  return gitExec("git rev-parse --abbrev-ref HEAD");
}

function discoverAllChanges(diffFrom) {
  const diffFiles = getChangedFiles(diffFrom);
  const staged = getStagedFiles();
  const untracked = getUntrackedFiles();
  const seen = new Set();
  const result = [];
  for (const f of [...diffFiles, ...staged, ...untracked]) {
    if (!seen.has(f)) {
      seen.add(f);
      result.push(f);
    }
  }
  return result;
}

// --- File classification ---

// Language-specific classifiers. Each returns { category, riskWeight }
// for a given file path, or null if not applicable.
const CLASSIFIERS = [
  // Test files
  {
    match: (f) => /\.(test|spec)\.[jt]sx?$/.test(f) || /[/\\]__tests__[/\\]/.test(f) || /[/\\]test[/\\]/.test(f),
    classify: () => ({ category: "test", riskWeight: 0.3 }),
  },
  // C# test files
  {
    match: (f) => /Tests?\.cs$/.test(f) || /[/\\]Tests?[/\\]/.test(f),
    classify: () => ({ category: "test", riskWeight: 0.3 }),
  },
  // Migration / schema files
  {
    match: (f) => /migration/i.test(f) || /schema/i.test(f) || f.endsWith(".sql"),
    classify: () => ({ category: "migration", riskWeight: 1.5 }),
  },
  // Configuration files
  {
    match: (f) => /\.(json|ya?ml|toml|ini|env|config)$/i.test(f) || f.includes("Dockerfile") || f.includes("docker-compose"),
    classify: () => ({ category: "config", riskWeight: 0.8 }),
  },
  // Security-sensitive paths
  {
    match: (f) => /auth|permission|secret|token|crypt|security|credential/i.test(f),
    classify: () => ({ category: "security-sensitive", riskWeight: 2 }),
  },
  // API surface
  {
    match: (f) => /controller|route|endpoint|handler|api/i.test(f),
    classify: () => ({ category: "api", riskWeight: 1.2 }),
  },
  // Documentation
  {
    match: (f) => f.endsWith(".md") || f.endsWith(".txt") || f.endsWith(".rst"),
    classify: () => ({ category: "docs", riskWeight: 0.1 }),
  },
  // Source code (default)
  {
    match: () => true,
    classify: () => ({ category: "source", riskWeight: 1 }),
  },
];

function classifyFile(filePath) {
  for (const c of CLASSIFIERS) {
    if (c.match(filePath)) return c.classify(filePath);
  }
  return { category: "source", riskWeight: 1 };
}

function classifyFiles(files) {
  return files.map((f) => {
    const { category, riskWeight } = classifyFile(f);
    return { path: f, category, riskWeight };
  });
}

// --- Risk markers ---

function computeRiskMarkers(classified) {
  const markers = [];

  const secFiles = classified.filter((f) => f.category === "security-sensitive");
  if (secFiles.length > 0) {
    markers.push({ level: "high", marker: `Security-sensitive files changed: ${secFiles.map((f) => f.path).join(", ")}` });
  }

  const migrations = classified.filter((f) => f.category === "migration");
  if (migrations.length > 0) {
    markers.push({ level: "high", marker: `Migration/schema changes: ${migrations.map((f) => f.path).join(", ")}` });
  }

  const apiFiles = classified.filter((f) => f.category === "api");
  if (apiFiles.length > 0) {
    markers.push({ level: "medium", marker: `API surface changes: ${apiFiles.map((f) => f.path).join(", ")}` });
  }

  const sourceFiles = classified.filter((f) => f.category === "source");
  const testFiles = classified.filter((f) => f.category === "test");
  if (sourceFiles.length > 0 && testFiles.length === 0) {
    markers.push({ level: "medium", marker: "Source files changed with no corresponding test changes" });
  }

  if (classified.length > 20) {
    markers.push({ level: "medium", marker: `Large changeset: ${classified.length} files` });
  }

  return markers;
}

// --- Context neighborhood ---

function findContextNeighborhood(changedFiles) {
  // Find directories that contain changed files — these are the "neighborhood"
  const dirs = new Set();
  for (const f of changedFiles) {
    const dir = path.dirname(f);
    if (dir !== ".") dirs.add(dir);
  }

  const neighborhood = [];
  for (const dir of [...dirs].sort((a, b) => a.localeCompare(b))) {
    try {
      const entries = fs.readdirSync(dir).filter((e) => !e.startsWith("."));
      const changedInDir = changedFiles.filter((f) => path.dirname(f) === dir).map((f) => path.basename(f));
      neighborhood.push({
        directory: dir,
        totalFiles: entries.length,
        changedFiles: changedInDir,
        unchangedFiles: entries.filter((e) => !changedInDir.includes(e)).slice(0, 10),
      });
    } catch {
      // Directory might not exist yet or be inaccessible
    }
  }
  return neighborhood;
}

// --- Evidence availability ---

function checkEvidence(changedFiles) {
  const evidence = {
    hasTests: changedFiles.some((f) => classifyFile(f).category === "test"),
    hasDocs: changedFiles.some((f) => classifyFile(f).category === "docs"),
    hasMigrations: changedFiles.some((f) => classifyFile(f).category === "migration"),
    hasConfig: changedFiles.some((f) => classifyFile(f).category === "config"),
  };

  // Check for common evidence files
  evidence.hasImpldoc = true; // We always have the impldoc
  evidence.hasTrivyResults = fs.existsSync(".trivy-results.json");
  evidence.hasReviewFindings = false; // Will be set from impldoc parsing

  return evidence;
}

// --- Impldoc parsing ---

function parseImpldoc(impldocPath) {
  const content = fs.readFileSync(impldocPath, "utf8");
  const lines = content.split("\n");

  const info = {
    title: "",
    hasRequirements: false,
    hasAcceptanceCriteria: false,
    hasTestingStrategy: false,
    hasReviewFindings: false,
    hasQualityReport: false,
    hasChangeLog: false,
    sections: [],
  };

  let currentSection = "";
  for (const line of lines) {
    const h1 = line.match(/^# (.+)/);
    const h2 = line.match(/^## (.+)/);
    if (h1 && !info.title) info.title = h1[1].trim();
    if (h2) {
      currentSection = h2[1].trim();
      info.sections.push(currentSection);
    }
    if (currentSection === "Requirements" && line.startsWith("- [")) info.hasRequirements = true;
    if (currentSection === "Acceptance Criteria" && line.startsWith("- [")) info.hasAcceptanceCriteria = true;
    if (currentSection === "Testing Strategy") info.hasTestingStrategy = true;
    if (currentSection === "Review Findings") info.hasReviewFindings = true;
    if (currentSection === "Quality Report") info.hasQualityReport = true;
    if (currentSection === "Change Log") info.hasChangeLog = true;
  }

  return info;
}

// --- Recommended read order ---

function computeReadOrder(classified, riskMarkers) {
  // Sort by risk weight descending, then alphabetically
  const sorted = [...classified].sort((a, b) => {
    if (b.riskWeight !== a.riskWeight) return b.riskWeight - a.riskWeight;
    return a.path.localeCompare(b.path);
  });

  // Group by category for the recommendation
  const groups = [];
  const seen = new Set();
  for (const f of sorted) {
    if (!seen.has(f.category)) {
      seen.add(f.category);
      const filesInCategory = sorted.filter((x) => x.category === f.category);
      groups.push({ category: f.category, files: filesInCategory.map((x) => x.path) });
    }
  }

  return groups;
}

// --- Output formatting ---

function formatFrontmatter(meta) {
  const lines = ["---"];
  for (const [key, value] of Object.entries(meta)) {
    if (typeof value === "object") {
      lines.push(`${key}: ${JSON.stringify(value)}`);
    } else {
      lines.push(`${key}: ${value}`);
    }
  }
  lines.push("---");
  return lines.join("\n");
}

function formatPacket(data) {
  const { meta, impldocInfo, classified, riskMarkers, neighborhood, evidence, readOrder, diffStat, untrackedCount } = data;

  const lines = [];

  // Frontmatter
  lines.push(formatFrontmatter(meta), "");

  // Title
  lines.push(`# Review Packet: ${impldocInfo.title}`, "");

  // Change summary
  lines.push(
    "## Change Summary",
    "",
    `- **Impldoc**: ${meta.impldoc}`,
    `- **Branch**: ${meta.branch}`,
    `- **Commit**: ${meta.commit}`,
    `- **Files changed**: ${classified.length}`,
    `- **Diff base**: ${meta.diffFrom}`,
    "",
  );

  if (diffStat) {
    lines.push("### Diff Stats", "", "```", diffStat, "```");
    if (untrackedCount > 0) {
      lines.push("", `> **Scope note**: ${untrackedCount} untracked file(s) are included in the file list but not reflected in the diff stats above.`);
    }
    lines.push("");
  }

  // File breakdown by category
  lines.push("### Files by Category", "");
  const categories = {};
  for (const f of classified) {
    if (!categories[f.category]) categories[f.category] = [];
    categories[f.category].push(f.path);
  }
  for (const [cat, files] of Object.entries(categories).sort((a, b) => a[0].localeCompare(b[0]))) {
    lines.push(`**${cat}** (${files.length}):`);
    for (const f of files) {
      lines.push(`- ${f}`);
    }
    lines.push("");
  }

  // Risk markers
  lines.push("## Risk Markers", "");
  if (riskMarkers.length === 0) {
    lines.push("No elevated risk markers detected.");
  } else {
    for (const r of riskMarkers) {
      const icon = r.level === "high" ? "\u26A0\uFE0F" : "\u2139\uFE0F";
      lines.push(`- ${icon} **${r.level.toUpperCase()}**: ${r.marker}`);
    }
  }
  lines.push("");

  // Context neighborhood
  lines.push("## Context Neighborhood", "");
  if (neighborhood.length === 0) {
    lines.push("No neighboring context available.");
  } else {
    for (const n of neighborhood) {
      lines.push(`### ${n.directory}/ (${n.totalFiles} files, ${n.changedFiles.length} changed)`, "", "Changed:");
      for (const f of n.changedFiles) lines.push(`- \u2705 ${f}`);
      if (n.unchangedFiles.length > 0) {
        lines.push("");
        lines.push("Nearby (unchanged):");
        for (const f of n.unchangedFiles) lines.push(`- ${f}`);
      }
      lines.push("");
    }
  }

  // Evidence availability
  lines.push(
    "## Evidence Availability",
    "",
    `| Evidence | Available |`,
    `| --- | --- |`,
    `| Impldoc | ${evidence.hasImpldoc ? "Yes" : "No"} |`,
    `| Tests in changeset | ${evidence.hasTests ? "Yes" : "No"} |`,
    `| Documentation changes | ${evidence.hasDocs ? "Yes" : "No"} |`,
    `| Migration/schema | ${evidence.hasMigrations ? "Yes" : "No"} |`,
    `| Trivy scan results | ${evidence.hasTrivyResults ? "Yes" : "No"} |`,
    `| Impldoc review findings | ${impldocInfo.hasReviewFindings ? "Yes" : "No"} |`,
    `| Impldoc quality report | ${impldocInfo.hasQualityReport ? "Yes" : "No"} |`,
    "",
  );

  // Recommended read order
  lines.push("## Recommended Read Order", "");
  let order = 1;
  for (const group of readOrder) {
    lines.push(`${order}. **${group.category}** files:`);
    for (const f of group.files) {
      lines.push(`   - ${f}`);
    }
    order++;
  }
  lines.push("");

  // Impldoc completeness
  lines.push(
    "## Impldoc Completeness",
    "",
    `| Section | Present |`,
    `| --- | --- |`,
    `| Requirements | ${impldocInfo.hasRequirements ? "Yes" : "No"} |`,
    `| Acceptance Criteria | ${impldocInfo.hasAcceptanceCriteria ? "Yes" : "No"} |`,
    `| Testing Strategy | ${impldocInfo.hasTestingStrategy ? "Yes" : "No"} |`,
    `| Review Findings | ${impldocInfo.hasReviewFindings ? "Yes" : "No"} |`,
    `| Quality Report | ${impldocInfo.hasQualityReport ? "Yes" : "No"} |`,
    `| Change Log | ${impldocInfo.hasChangeLog ? "Yes" : "No"} |`,
    "",
  );

  lines.push("---", `_Generated by tsp-review-packet v${VERSION}_`);

  return lines.join("\n");
}

// --- Main ---

function main() {
  const args = parseArgs(process.argv);

  if (!fs.existsSync(args.impldocPath)) {
    if (!args.quiet) console.error(`Impldoc not found: ${args.impldocPath}`);
    process.exit(1);
  }

  const diffFrom = resolveDiffFrom(args.diffFrom);
  const impldocInfo = parseImpldoc(args.impldocPath);
  const changedFiles = discoverAllChanges(diffFrom);
  const classified = classifyFiles(changedFiles);
  const riskMarkers = computeRiskMarkers(classified);
  const neighborhood = findContextNeighborhood(changedFiles);
  const evidence = checkEvidence(changedFiles);
  const readOrder = computeReadOrder(classified, riskMarkers);
  const diffStat = getDiffStat(diffFrom);
  const untrackedCount = getUntrackedFiles().filter((f) => changedFiles.includes(f)).length;

  const meta = {
    generator: "tsp-review-packet",
    version: VERSION,
    impldoc: args.impldocPath,
    diffFrom: diffFrom,
    commit: getCurrentCommit(),
    branch: getCurrentBranch(),
    timestamp: new Date().toISOString(),
    fileCount: changedFiles.length,
    riskLevel: riskMarkers.some((r) => r.level === "high")
      ? "high"
      : riskMarkers.length > 0 ? "medium" : "low",
  };

  const output = formatPacket({
    meta,
    impldocInfo,
    classified,
    riskMarkers,
    neighborhood,
    evidence,
    readOrder,
    diffStat,
    untrackedCount,
  });

  // Determine output path
  let outputPath;
  if (args.output) {
    outputPath = args.output;
  } else {
    // Default: .tsp-copilot/cache/{basename}-review-packet.md
    const impldocBase = path.basename(args.impldocPath, ".md");
    outputPath = path.join(".tsp-copilot", "cache", `${impldocBase}-review-packet.md`);
  }

  const dir = path.dirname(outputPath);
  if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(outputPath, output);

  if (!args.quiet) {
    console.log(`Review packet written to ${outputPath}`);
  }
}

// --- Exports for testing ---

if (require.main === module) {
  main();
}

module.exports = {
  parseArgs,
  classifyFile,
  classifyFiles,
  computeRiskMarkers,
  findContextNeighborhood,
  checkEvidence,
  parseImpldoc,
  computeReadOrder,
  formatFrontmatter,
  formatPacket,
  getChangedFiles,
  getStagedFiles,
  getUntrackedFiles,
  getMergeBase,
  resolveDiffFrom,
  discoverAllChanges,
  getDiffStat,
  CLASSIFIERS,
  VERSION,
};
