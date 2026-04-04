#!/usr/bin/env node
"use strict";

// TSP Copilot — Code Index Generator
// Generates a file inventory with descriptions extracted from file-level docstrings,
// a visual file tree, and coverage statistics for AI agent context.
// Usage: node tsp-code-index.js [--output <path>] [--root <dir>]
// Exit codes: 0 = success, 1 = git error, 2 = invalid arguments

const fs = require("node:fs");
const path = require("node:path");
const { execSync } = require("node:child_process");

const VERSION = "3.0.0";
const MAX_READ_BYTES = 8192;
const MAX_DESC_LEN = 400;

// --- Argument parsing ---

function parseArgs(argv) {
  const args = { output: ".tsp-copilot/cache/code-index.md", root: ".", quiet: false };
  let i = 2;
  while (i < argv.length) {
    if (argv[i] === "--output" && argv[i + 1]) {
      args.output = argv[i + 1];
      i += 2;
    } else if (argv[i] === "--root" && argv[i + 1]) {
      args.root = argv[i + 1];
      i += 2;
    } else if (argv[i] === "--quiet") {
      args.quiet = true;
      i++;
    } else {
      console.error(`Unknown option: ${argv[i]}`);
      process.exit(2);
    }
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

function getCurrentCommit() {
  return gitExec("git rev-parse HEAD");
}

function getShortCommit() {
  return gitExec("git rev-parse --short HEAD");
}

function getTrackedFiles(root) {
  const output = gitExec(`git ls-files ${root}`);
  if (!output) return [];
  return output.split("\n").filter(Boolean);
}

function getUntrackedFiles(root) {
  const output = gitExec(`git ls-files --others --exclude-standard ${root}`);
  if (!output) return [];
  return output.split("\n").filter(Boolean);
}

function getAllFiles(root) {
  const tracked = getTrackedFiles(root);
  const untracked = getUntrackedFiles(root);
  const seen = new Set(tracked);
  const result = [...tracked];
  for (const f of untracked) {
    if (!seen.has(f)) {
      seen.add(f);
      result.push(f);
    }
  }
  return result;
}

// --- Docstring extraction ---

/**
 * Read the first MAX_READ_BYTES of a file to extract the leading docstring.
 * Returns the raw text or null.
 */
function readFileHead(filePath) {
  try {
    const fd = fs.openSync(filePath, "r");
    const buf = Buffer.alloc(MAX_READ_BYTES);
    const bytesRead = fs.readSync(fd, buf, 0, MAX_READ_BYTES, 0);
    fs.closeSync(fd);
    return buf.toString("utf8", 0, bytesRead);
  } catch {
    return null;
  }
}

function cleanDocstring(text) {
  if (!text) return null;
  const cleaned = text
    .replaceAll("\r\n", "\n")
    .replaceAll("\n", " ")
    .replaceAll(/\s+/g, " ")
    .trim();
  if (cleaned.length === 0) return null;
  return cleaned.length > MAX_DESC_LEN
    ? cleaned.slice(0, MAX_DESC_LEN - 1) + "\u2026"
    : cleaned;
}

// Each extractor returns the cleaned docstring text or null.
const DOCSTRING_EXTRACTORS = [
  // JavaScript / TypeScript — consecutive // lines or /* */ or /** */ before code
  {
    extensions: [".js", ".ts", ".jsx", ".tsx", ".mjs", ".cjs"],
    extract(head) {
      const lines = head.split("\n");
      const commentLines = [];
      for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed === "" || trimmed.startsWith("#!")) continue; // skip blank/shebang
        if (trimmed.startsWith("//")) {
          commentLines.push(trimmed.replace(/^\/\/\s?/, ""));
        } else if (trimmed.startsWith("/*") || trimmed.startsWith("/**")) {
          // Block comment — extract until */
          const blockMatch = head.match(/\/\*\*?\s*([\s\S]*?)\*\//);
          if (blockMatch) {
            const blockText = blockMatch[1]
              .split("\n")
              .map((l) => l.trim().replace(/^\*\s?/, "").trim())
              .filter(Boolean)
              .join(" ");
            return cleanDocstring(blockText);
          }
          break;
        } else if (trimmed.startsWith('"use strict"') || trimmed.startsWith("'use strict'")) {
          continue; // skip "use strict" directive
        } else {
          break; // hit real code
        }
      }
      if (commentLines.length === 0) return null;
      return cleanDocstring(commentLines.join(" "));
    },
  },

  // C# — /// <summary> XML doc or leading // block before using/namespace
  {
    extensions: [".cs"],
    extract(head) {
      // Try XML doc summary first
      const xmlMatch = head.match(/<summary>\s*([\s\S]*?)\s*<\/summary>/);
      if (xmlMatch) {
        const text = xmlMatch[1]
          .split("\n")
          .map((l) => l.trim().replace(/^\/\/\/\s?/, "").trim())
          .filter(Boolean)
          .join(" ");
        return cleanDocstring(text);
      }
      // Fall back to leading // block
      const lines = head.split("\n");
      const commentLines = [];
      for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed === "") continue;
        if (trimmed.startsWith("//")) {
          commentLines.push(trimmed.replace(/^\/\/\s?/, ""));
        } else {
          break;
        }
      }
      if (commentLines.length === 0) return null;
      return cleanDocstring(commentLines.join(" "));
    },
  },

  // Python — module docstring """...""" or '''...'''
  {
    extensions: [".py"],
    extract(head) {
      // Skip shebang and encoding lines
      const content = head.replace(/^#!.*\n/, "").replace(/^#.*?coding.*\n/m, "");
      const tripleMatch = content.match(/^\s*(?:"""([\s\S]*?)"""|'''([\s\S]*?)''')/);
      if (!tripleMatch) return null;
      const text = (tripleMatch[1] || tripleMatch[2]).trim();
      return cleanDocstring(text);
    },
  },

  // Go — // comment block before package declaration
  {
    extensions: [".go"],
    extract(head) {
      const lines = head.split("\n");
      const commentLines = [];
      for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed === "") {
          if (commentLines.length > 0) break; // end of comment block
          continue;
        }
        if (trimmed.startsWith("//")) {
          commentLines.push(trimmed.replace(/^\/\/\s?/, ""));
        } else {
          break;
        }
      }
      if (commentLines.length === 0) return null;
      return cleanDocstring(commentLines.join(" "));
    },
  },

  // Rust — //! inner doc comments
  {
    extensions: [".rs"],
    extract(head) {
      const lines = head.split("\n");
      const commentLines = [];
      for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed === "") continue;
        if (trimmed.startsWith("//!")) {
          commentLines.push(trimmed.replace(/^\/\/!\s?/, ""));
        } else {
          break;
        }
      }
      if (commentLines.length === 0) return null;
      return cleanDocstring(commentLines.join(" "));
    },
  },

  // Java — /** Javadoc */ before class
  {
    extensions: [".java"],
    extract(head) {
      const blockMatch = head.match(/\/\*\*\s*([\s\S]*?)\*\//);
      if (!blockMatch) return null;
      const text = blockMatch[1]
        .split("\n")
        .map((l) => l.trim().replace(/^\*\s?/, "").trim())
        .filter((l) => l && !l.startsWith("@")) // skip @param, @return, etc.
        .join(" ");
      return cleanDocstring(text);
    },
  },

  // Shell — # comment block after shebang
  {
    extensions: [".sh", ".bash", ".zsh"],
    extract(head) {
      const lines = head.split("\n");
      const commentLines = [];
      let pastShebang = false;
      for (const line of lines) {
        const trimmed = line.trim();
        if (trimmed.startsWith("#!")) { pastShebang = true; continue; }
        if (trimmed === "") {
          if (pastShebang && commentLines.length > 0) { break; }
          continue;
        }
        if (trimmed.startsWith("#")) {
          commentLines.push(trimmed.replace(/^#\s?/, ""));
          pastShebang = true;
        } else {
          break;
        }
      }
      if (commentLines.length === 0) return null;
      return cleanDocstring(commentLines.join(" "));
    },
  },

  // Markdown — first # heading
  {
    extensions: [".md", ".mdx"],
    extract(head) {
      const lines = head.split("\n");
      for (const line of lines) {
        const match = line.match(/^#\s+(.+)/);
        if (match) return cleanDocstring(match[1].trim());
      }
      return null;
    },
  },
];

function extractDocstring(filePath) {
  const ext = path.extname(filePath).toLowerCase();
  const extractor = DOCSTRING_EXTRACTORS.find((e) => e.extensions.includes(ext));
  if (!extractor) return null;

  const head = readFileHead(filePath);
  if (!head) return null;

  return extractor.extract(head);
}

// --- File description heuristics ---

// Language-specific describers. Each returns a short description or null.
const DESCRIBERS = [
  // Package manifests
  { match: (f) => f === "package.json", describe: () => "Node.js package manifest" },
  { match: (f) => f === "tsconfig.json", describe: () => "TypeScript configuration" },
  { match: (f) => f.endsWith(".csproj"), describe: (f) => `C# project: ${path.basename(f, ".csproj")}` },
  { match: (f) => f.endsWith(".sln"), describe: () => "Visual Studio solution" },
  { match: (f) => f === "Cargo.toml", describe: () => "Rust package manifest" },
  { match: (f) => f === "go.mod", describe: () => "Go module definition" },
  { match: (f) => /requirements.*\.txt$/.test(f), describe: () => "Python dependencies" },
  { match: (f) => f === "pyproject.toml", describe: () => "Python project configuration" },

  // Lock files
  { match: (f) => /(lock|\.lock$)/i.test(f) && !/(auth|permission)/i.test(f), describe: () => "Dependency lock file" },

  // Docker
  { match: (f) => /^Dockerfile/i.test(path.basename(f)), describe: () => "Container image definition" },
  { match: (f) => /docker-compose/i.test(f), describe: () => "Docker Compose orchestration" },

  // CI/CD
  { match: (f) => /\.github\/workflows\//.test(f), describe: () => "GitHub Actions workflow" },
  { match: (f) => /\.gitlab-ci/i.test(f), describe: () => "GitLab CI pipeline" },
  { match: (f) => /Jenkinsfile/i.test(path.basename(f)), describe: () => "Jenkins pipeline" },

  // Documentation
  { match: (f) => /^README/i.test(path.basename(f)), describe: () => "Project documentation" },
  { match: (f) => f === "CHANGELOG.md" || f === "CHANGES.md", describe: () => "Release changelog" },
  { match: (f) => f === "LICENSE" || f === "LICENSE.md", describe: () => "License file" },
  { match: (f) => f === "architecture.md", describe: () => "Architecture document (human-authored)" },
  { match: (f) => f === "features.md", describe: () => "Feature inventory" },
  { match: (f) => f === "roadmap.md", describe: () => "Project roadmap" },
  { match: (f) => f === "todos.md", describe: () => "Deferred work tracker" },

  // Impldocs
  { match: (f) => f.endsWith("impldocs/INDEX.md"), describe: () => "Implementation document registry" },
  { match: (f) => /impldocs\/.*-review\.md$/.test(f), describe: () => "Review artifact" },
  { match: (f) => /impldocs\/.*-review-packet\.md$/.test(f), describe: () => "Review packet" },
  { match: (f) => /impldocs\/\d{3}-/.test(f) && f.endsWith(".md"), describe: (f) => `Implementation document: ${path.basename(f, ".md")}` },

  // Tests
  { match: (f) => /\.(test|spec)\.[jt]sx?$/.test(f), describe: () => "Test file" },
  { match: (f) => f.endsWith("Test.cs") || f.endsWith("Tests.cs"), describe: () => "C# test file" },
  { match: (f) => f.endsWith("_test.go"), describe: () => "Go test file" },
  { match: (f) => /test_.*\.py$/.test(f) || /.*_test\.py$/.test(f), describe: () => "Python test file" },

  // Migrations
  { match: (f) => /migration/i.test(f), describe: () => "Database migration" },

  // Config files
  { match: (f) => /\.(json|ya?ml|toml|ini)$/i.test(f), describe: () => "Configuration file" },
  { match: (f) => /\.env/.test(path.basename(f)), describe: () => "Environment configuration" },

  // Source by extension
  { match: (f) => /\.[jt]sx?$/.test(f), describe: () => "Source file" },
  { match: (f) => f.endsWith(".cs"), describe: () => "C# source file" },
  { match: (f) => f.endsWith(".go"), describe: () => "Go source file" },
  { match: (f) => f.endsWith(".py"), describe: () => "Python source file" },
  { match: (f) => f.endsWith(".rs"), describe: () => "Rust source file" },
  { match: (f) => f.endsWith(".java"), describe: () => "Java source file" },
  { match: (f) => /\.(css|scss|less)$/.test(f), describe: () => "Stylesheet" },
  { match: (f) => f.endsWith(".htm") || f.endsWith(".html"), describe: () => "HTML file" },
  { match: (f) => f.endsWith(".sql"), describe: () => "SQL file" },
  { match: (f) => f.endsWith(".sh"), describe: () => "Shell script" },
  { match: (f) => f.endsWith(".md"), describe: () => "Documentation" },
];

function describeHeuristic(filePath) {
  for (const d of DESCRIBERS) {
    if (d.match(filePath)) return d.describe(filePath);
  }
  return null;
}

/**
 * Describe a file using docstring extraction first, then heuristic fallback.
 * Returns { description, source } where source is "docstring", "heuristic", or null.
 */
function describeFile(filePath) {
  const docstring = extractDocstring(filePath);
  if (docstring) return { description: docstring, source: "docstring" };

  const heuristic = describeHeuristic(filePath);
  if (heuristic) return { description: heuristic, source: "heuristic" };

  return { description: null, source: null };
}

// --- Tags ---

function tagFile(filePath) {
  const tags = [];
  const base = path.basename(filePath);
  const dir = path.dirname(filePath);

  if (/\.(test|spec)\./i.test(base) || base.endsWith("Test.cs") || base.endsWith("Tests.cs") || base.endsWith("_test.go")) tags.push("test");
  if (/migration/i.test(filePath) || base.endsWith(".sql")) tags.push("data");
  if (/auth|permission|secret|token|crypt|security/i.test(filePath)) tags.push("security");
  if (/controller|route|endpoint|handler|api/i.test(filePath)) tags.push("api");
  if (/config|\.env|\.json$|\.ya?ml$|\.toml$/i.test(base)) tags.push("config");
  if (base.endsWith(".md")) tags.push("docs");
  if (/\.github\/(workflows|actions)/i.test(dir)) tags.push("ci");
  if (/Dockerfile|docker-compose/i.test(base)) tags.push("infra");

  return tags;
}

// --- File tree rendering ---

function renderFileTree(filePaths) {
  // Build a nested structure
  const root = {};
  for (const fp of filePaths) {
    const parts = fp.split("/");
    let node = root;
    for (const part of parts) {
      if (!node[part]) node[part] = {};
      node = node[part];
    }
  }

  const lines = ["## File Tree", "", "```"];

  function render(node, prefix) {
    const entries = Object.keys(node).sort((a, b) => {
      // Directories first (have children), then files
      const aIsDir = Object.keys(node[a]).length > 0;
      const bIsDir = Object.keys(node[b]).length > 0;
      if (aIsDir !== bIsDir) return aIsDir ? -1 : 1;
      return a.localeCompare(b);
    });
    for (let i = 0; i < entries.length; i++) {
      const name = entries[i];
      const isLast = i === entries.length - 1;
      const connector = isLast ? "\u2514\u2500\u2500 " : "\u251C\u2500\u2500 ";
      const childPrefix = isLast ? "    " : "\u2502   ";
      const children = Object.keys(node[name]);
      const displayName = children.length > 0 ? name + "/" : name;
      lines.push(prefix + connector + displayName);
      if (children.length > 0) {
        render(node[name], prefix + childPrefix);
      }
    }
  }

  render(root, "");
  lines.push("```", "");
  return lines;
}

// --- Coverage statistics ---

function computeStats(files) {
  let docstring = 0;
  let heuristic = 0;
  let undescribed = 0;
  for (const f of files) {
    if (f.source === "docstring") docstring++;
    else if (f.source === "heuristic") heuristic++;
    else undescribed++;
  }
  return { total: files.length, docstring, heuristic, undescribed };
}

// --- Output formatting ---

function formatIndex(files, meta) {
  const lines = [];

  // Frontmatter
  lines.push(
    "---",
    `generator: tsp-code-index`,
    `version: ${VERSION}`,
    `timestamp: ${meta.timestamp}`,
    `fileCount: ${files.length}`,
    "---",
    "",
  );

  lines.push("# Code Index", "", `Generated at ${meta.timestamp}.`, "");

  // Coverage stats
  const stats = computeStats(files);
  lines.push(`${stats.total} files | ${stats.docstring} with docstrings | ${stats.heuristic} with heuristic descriptions | ${stats.undescribed} undescribed`, "");

  // File tree
  const treePaths = files.map((f) => f.path);
  lines.push(...renderFileTree(treePaths));

  // Group files by top-level directory
  const groups = {};
  for (const f of files) {
    const parts = f.path.split("/");
    const group = parts.length > 1 ? parts[0] + "/" : "(root)";
    if (!groups[group]) groups[group] = [];
    groups[group].push(f);
  }

  for (const [group, groupFiles] of Object.entries(groups).sort((a, b) => a[0].localeCompare(b[0]))) {
    lines.push(`## ${group}`, "", "| File | Description | Tags |", "| --- | --- | --- |");
    for (const f of groupFiles) {
      const desc = f.description || "";
      const tags = f.tags.length > 0 ? f.tags.map((t) => `\`${t}\``).join(" ") : "";
      lines.push(`| ${f.path} | ${desc} | ${tags} |`);
    }
    lines.push("");
  }

  lines.push("---", `_Generated by tsp-code-index v${VERSION}_`);

  return lines.join("\n");
}

// --- Main ---

function main() {
  const args = parseArgs(process.argv);

  const allFiles = getAllFiles(args.root);
  if (allFiles.length === 0) {
    if (!args.quiet) console.error("No git-tracked files found. Is this a git repository?");
    process.exit(1);
  }

  const files = allFiles.map((f) => {
    const { description, source } = describeFile(f);
    return { path: f, description, source, tags: tagFile(f) };
  });

  const meta = {
    timestamp: new Date().toISOString(),
  };

  const output = formatIndex(files, meta);

  // Ensure output directory exists
  const dir = path.dirname(args.output);
  if (dir && !fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });

  fs.writeFileSync(args.output, output);

  if (!args.quiet) {
    const stats = computeStats(files);
    const statsMsg = `${stats.total} files (${stats.docstring} docstrings, ${stats.heuristic} heuristic, ${stats.undescribed} undescribed)`;
    console.log(`Code index written: ${statsMsg}. Output: ${args.output}`);
  }
}

// --- Exports for testing ---

if (require.main === module) {
  main();
}

module.exports = {
  extractDocstring,
  describeHeuristic,
  describeFile,
  tagFile,
  renderFileTree,
  computeStats,
  formatIndex,
  cleanDocstring,
  parseArgs,
  getTrackedFiles,
  getUntrackedFiles,
  getAllFiles,
  DOCSTRING_EXTRACTORS,
  DESCRIBERS,
  VERSION,
};
