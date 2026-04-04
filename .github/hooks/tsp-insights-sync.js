#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const repoHelpers = require("../scripts/tsp-repo-helpers");

const DEFAULT_INSIGHTS_TEMPLATE = `# Conversation Insights

> Raw insights captured from AI conversations. Review with \`/tsp-insights\` to promote to project, user, or org instructions.

## Untriaged

<!-- Capped at 20 items. Each entry has a hidden insightId comment above it. Low-confidence entries are dropped when the cap is reached. -->

## Promoted

<!-- Each entry keeps its insightId comment for dedup. Entries are retained permanently so capture-time dedup can detect already-promoted preferences. -->

## Dismissed

<!-- Each entry keeps its insightId comment for dedup. Entries are retained permanently so capture-time dedup can detect previously-dismissed preferences. -->

## Aliases

<!-- Maps variant insightIds to a primary insightId. Agents read this during dedup; only /tsp-insights creates entries. Format: \`alias → primary\` -->
`;

const LOCK_RETRY_COUNT = 5;
const LOCK_RETRY_DELAY_MS = 100;
const LOCK_STALE_MS = 60_000;
const SECTION_NAMES = ["Untriaged", "Promoted", "Dismissed", "Aliases"];

function readHookInput() {
  const raw = fs.readFileSync(0, "utf8");
  if (!raw?.trim()) throw new Error("Hook stdin was empty");
  return JSON.parse(raw);
}

function normalizeInput(input) {
  return {
    timestamp: input.timestamp,
    hookEventName: input.hookEventName || input.hook_event_name,
    sessionId: input.sessionId || input.session_id,
    cwd: input.cwd || process.cwd(),
  };
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function emitWarnings(warnings) {
  for (const warning of warnings) {
    process.stderr.write(`[tsp-insights-sync] ${warning}\n`);
  }
}

function readTextIfExists(filePath) {
  if (!fs.existsSync(filePath)) return null;
  return fs.readFileSync(filePath, "utf8");
}

function writeTextAtomic(filePath, content) {
  repoHelpers.ensureDir(path.dirname(filePath));
  const tempPath = `${filePath}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(tempPath, content);
  fs.renameSync(tempPath, filePath);
}

function trimTrailingBlankLines(lines) {
  const next = [...lines];
  while (next.length > 0 && next.at(-1).trim() === "") {
    next.pop();
  }
  return next;
}

function ensureBlankLine(lines) {
  if (lines.length > 0 && lines.at(-1) !== "") {
    lines.push("");
  }
}

function isInsightIdLine(line) {
  return /^\s*<!--\s*insightId:/.test(line);
}

function extractInsightId(line) {
  const match = line.match(/<!--\s*insightId:([^ >]+).*-->/);
  return match ? match[1].trim() : null;
}

function extractCanonical(bodyLines) {
  const bulletLine = bodyLines.find((line) => line.trim().startsWith("- "));
  if (!bulletLine) return trimTrailingBlankLines(bodyLines).join("\n").trim();

  let canonical = bulletLine.trim().replace(/^-\s+/, "");
  canonical = canonical.replace(/^\*\*\[[^\]]+\]\*\*\s*/, "");
  const dividerIndex = canonical.indexOf(" — ");
  if (dividerIndex >= 0) {
    canonical = canonical.slice(0, dividerIndex);
  }
  return canonical.trim();
}

function parseEntrySection(lines) {
  const prefixLines = [];
  const entries = [];

  for (let index = 0; index < lines.length;) {
    if (!isInsightIdLine(lines[index])) {
      prefixLines.push(lines[index]);
      index++;
      continue;
    }

    const commentLine = lines[index];
    const insightId = extractInsightId(commentLine);
    const bodyLines = [];
    index++;

    while (index < lines.length && !isInsightIdLine(lines[index])) {
      bodyLines.push(lines[index]);
      index++;
    }

    entries.push({
      insightId,
      commentLine,
      bodyLines: trimTrailingBlankLines(bodyLines),
      canonical: extractCanonical(bodyLines),
    });
  }

  return {
    prefixLines: trimTrailingBlankLines(prefixLines),
    entries,
  };
}

function parseAliasesSection(lines) {
  const prefixLines = [];
  const aliases = [];

  for (const line of lines) {
    const match = line.match(/^- `([^`]+)` → `([^`]+)`\s*$/);
    if (match) {
      aliases.push({ alias: match[1], primary: match[2] });
    } else {
      prefixLines.push(line);
    }
  }

  return {
    prefixLines: trimTrailingBlankLines(prefixLines),
    aliases,
  };
}

function parseInsightsDocument(content) {
  const source = String(content == null ? DEFAULT_INSIGHTS_TEMPLATE : content).replaceAll("\r\n", "\n");
  const lines = source.split("\n");
  const rawSections = Object.fromEntries(SECTION_NAMES.map((name) => [name, []]));
  const headerLines = [];
  let currentSection = null;

  for (const line of lines) {
    const sectionMatch = /^## (Untriaged|Promoted|Dismissed|Aliases)\s*$/.exec(line);
    if (sectionMatch) {
      currentSection = sectionMatch[1];
      continue;
    }

    if (currentSection === null) {
      headerLines.push(line);
    } else {
      rawSections[currentSection].push(line);
    }
  }

  return {
    headerLines: trimTrailingBlankLines(headerLines),
    sections: {
      Untriaged: parseEntrySection(rawSections.Untriaged),
      Promoted: parseEntrySection(rawSections.Promoted),
      Dismissed: parseEntrySection(rawSections.Dismissed),
      Aliases: parseAliasesSection(rawSections.Aliases),
    },
  };
}

function cloneEntry(entry) {
  return {
    insightId: entry.insightId,
    commentLine: entry.commentLine || `<!-- insightId:${entry.insightId} -->`,
    bodyLines: [...(entry.bodyLines || [])],
    canonical: entry.canonical || extractCanonical(entry.bodyLines || []),
  };
}

function appendAliasSection(lines, section) {
  if (section.aliases.length === 0) return;
  ensureBlankLine(lines);

  section.aliases.forEach((alias, index) => {
    lines.push(`- \`${alias.alias}\` → \`${alias.primary}\``);
    if (index < section.aliases.length - 1) {
      lines.push("");
    }
  });
}

function appendEntrySection(lines, section) {
  if (section.entries.length === 0) return;
  ensureBlankLine(lines);

  section.entries.forEach((entry, index) => {
    lines.push(entry.commentLine, ...entry.bodyLines);
    if (index < section.entries.length - 1) {
      lines.push("");
    }
  });
}

function renderInsightsDocument(doc) {
  const fallback = parseInsightsDocument(DEFAULT_INSIGHTS_TEMPLATE);
  const lines = [...(doc.headerLines.length > 0 ? doc.headerLines : fallback.headerLines)];

  for (const sectionName of SECTION_NAMES) {
    ensureBlankLine(lines);
    lines.push(`## ${sectionName}`);
    const section = doc.sections[sectionName];
    const fallbackSection = fallback.sections[sectionName];
    const prefixLines = section.prefixLines.length > 0 ? section.prefixLines : fallbackSection.prefixLines;
    lines.push(...prefixLines);

    if (sectionName === "Aliases") {
      appendAliasSection(lines, section);
      continue;
    }

    appendEntrySection(lines, section);
  }

  return trimTrailingBlankLines(lines).join("\n") + "\n";
}

function countInsights(doc) {
  return doc.sections.Untriaged.entries.length
    + doc.sections.Promoted.entries.length
    + doc.sections.Dismissed.entries.length
    + doc.sections.Aliases.aliases.length;
}

function findEntryRecord(doc, insightId) {
  for (const sectionName of SECTION_NAMES.slice(0, 3)) {
    const entry = doc.sections[sectionName].entries.find((candidate) => candidate.insightId === insightId);
    if (entry) {
      return { sectionName, entry };
    }
  }
  return null;
}

function removeEntry(doc, sectionName, insightId) {
  const section = doc.sections[sectionName];
  section.entries = section.entries.filter((entry) => entry.insightId !== insightId);
}

function appendEntry(doc, sectionName, entry) {
  doc.sections[sectionName].entries.push(cloneEntry(entry));
}

function mergeAliases(backendDoc, localDoc) {
  const seen = new Set(backendDoc.sections.Aliases.aliases.map((entry) => `${entry.alias}:${entry.primary}`));

  for (const alias of localDoc.sections.Aliases.aliases) {
    const key = `${alias.alias}:${alias.primary}`;
    if (seen.has(key)) continue;
    backendDoc.sections.Aliases.aliases.push({ ...alias });
    seen.add(key);
  }
}

function isAdvancedSection(sectionName) {
  return sectionName === "Promoted" || sectionName === "Dismissed";
}

function mergeLocalEntryIntoBackend(backendDoc, sectionName, localEntry, warnings) {
  const backendRecord = findEntryRecord(backendDoc, localEntry.insightId);

  if (!backendRecord) {
    appendEntry(backendDoc, sectionName, localEntry);
    return;
  }

  if (backendRecord.entry.canonical !== localEntry.canonical) {
    warnings.push(`backend text kept for insightId ${localEntry.insightId} because local wording differed`);
  }

  if (backendRecord.sectionName === sectionName) {
    return;
  }

  if (sectionName === "Untriaged" && isAdvancedSection(backendRecord.sectionName)) {
    return;
  }

  if (isAdvancedSection(sectionName) && backendRecord.sectionName === "Untriaged") {
    removeEntry(backendDoc, backendRecord.sectionName, localEntry.insightId);
    appendEntry(backendDoc, sectionName, localEntry);
    return;
  }

  if (isAdvancedSection(sectionName) && isAdvancedSection(backendRecord.sectionName)) {
    warnings.push(
      `backend state kept for insightId ${localEntry.insightId} because local state ${sectionName} conflicted with backend state ${backendRecord.sectionName}`
    );
  }
}

function mergeInsightsDocuments(backendContent, localContent, warnings = []) {
  const backendDoc = parseInsightsDocument(backendContent);
  const localDoc = parseInsightsDocument(localContent);
  mergeAliases(backendDoc, localDoc);

  for (const sectionName of SECTION_NAMES.slice(0, 3)) {
    for (const localEntry of localDoc.sections[sectionName].entries) {
      mergeLocalEntryIntoBackend(backendDoc, sectionName, localEntry, warnings);
    }
  }

  return renderInsightsDocument(backendDoc);
}

function parseSignals(content, warnings = []) {
  const records = [];
  for (const line of String(content || "").split(/\r?\n/)) {
    if (!line.trim()) continue;
    try {
      const record = JSON.parse(line);
      if (!record || typeof record.signalId !== "string" || record.signalId.length === 0) {
        warnings.push("ignored signal entry without a valid signalId");
        continue;
      }
      records.push(record);
    } catch {
      warnings.push("ignored invalid JSON line in signals.jsonl");
    }
  }
  return records;
}

function renderSignals(records) {
  return records.length > 0
    ? records.map((record) => JSON.stringify(record)).join("\n") + "\n"
    : "";
}

function mergeSignals(backendContent, localContent, warnings = []) {
  const merged = [];
  const seen = new Set();

  for (const record of [...parseSignals(backendContent, warnings), ...parseSignals(localContent, warnings)]) {
    if (seen.has(record.signalId)) continue;
    seen.add(record.signalId);
    merged.push(record);
  }

  return renderSignals(merged);
}

function normalizeSignals(content, warnings = []) {
  return renderSignals(parseSignals(content, warnings));
}

function normalizeInsights(content) {
  return renderInsightsDocument(parseInsightsDocument(content));
}

function isInsightsContentPopulated(content) {
  return countInsights(parseInsightsDocument(content)) > 0;
}

function isSignalsContentPopulated(content) {
  return parseSignals(content, []).length > 0;
}

function buildLockPayload(input) {
  return {
    pid: process.pid,
    hostname: os.hostname(),
    sessionId: input.sessionId || null,
    startedAt: new Date().toISOString(),
  };
}

function parseLockFile(lockPath) {
  const raw = readTextIfExists(lockPath);
  if (!raw) return null;
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

function isLockStale(lockData, now = Date.now(), staleMs = LOCK_STALE_MS) {
  const startedAt = Date.parse(lockData?.startedAt || "");
  if (Number.isNaN(startedAt)) return true;
  return now - startedAt > staleMs;
}

async function acquireLock(lockPath, input, warnings = []) {
  repoHelpers.ensureDir(path.dirname(lockPath));

  for (let attempt = 0; attempt < LOCK_RETRY_COUNT; attempt++) {
    const payload = buildLockPayload(input);
    try {
      fs.writeFileSync(lockPath, JSON.stringify(payload, null, 2) + "\n", { flag: "wx" });
      return payload;
    } catch (err) {
      if (err.code !== "EEXIST") throw err;

      const existing = parseLockFile(lockPath);
      if (isLockStale(existing)) {
        fs.rmSync(lockPath, { force: true });
        warnings.push("reclaimed stale insights sync lock");
        continue;
      }

      if (attempt === LOCK_RETRY_COUNT - 1) {
        throw new Error("timed out waiting for insights sync lock");
      }

      await delay(LOCK_RETRY_DELAY_MS);
    }
  }

  throw new Error("unable to acquire insights sync lock");
}

function releaseLock(lockPath) {
  fs.rmSync(lockPath, { force: true });
}

async function withLock(lockPath, input, warnings, callback) {
  await acquireLock(lockPath, input, warnings);
  try {
    return await callback();
  } finally {
    releaseLock(lockPath);
  }
}

function getSyncPaths(repoContext) {
  const backendPaths = repoHelpers.getInsightsBackendPaths(repoContext);
  const localDir = path.join(repoContext.worktreeRoot, ".tsp-copilot");
  return {
    ...backendPaths,
    repoContext,
    localDir,
    localInsightsPath: path.join(localDir, "insights.md"),
    localSignalsPath: path.join(localDir, "signals.jsonl"),
  };
}

function isBackendPopulated(paths) {
  return isInsightsContentPopulated(readTextIfExists(paths.backendInsightsPath))
    || isSignalsContentPopulated(readTextIfExists(paths.backendSignalsPath));
}

function isLocalPopulated(paths) {
  return isInsightsContentPopulated(readTextIfExists(paths.localInsightsPath))
    || isSignalsContentPopulated(readTextIfExists(paths.localSignalsPath));
}

function ensureLocalMirror(paths, insightsContent, signalsContent) {
  repoHelpers.ensureDir(paths.localDir);
  writeTextAtomic(paths.localInsightsPath, normalizeInsights(insightsContent));
  writeTextAtomic(paths.localSignalsPath, normalizeSignals(signalsContent));
}

function ensureSyncRepoJson(repoContext) {
  return repoHelpers.ensureRepoJson(repoContext, {
    sync: {
      model: "session-boundary-mirror",
    },
  });
}

async function ensureBackendInitialized(paths, input, warnings) {
  ensureSyncRepoJson(paths.repoContext);
  if (isBackendPopulated(paths)) return;

  await withLock(paths.lockPath, input, warnings, async () => {
    ensureSyncRepoJson(paths.repoContext);
    if (isBackendPopulated(paths)) return;

    const localInsights = readTextIfExists(paths.localInsightsPath);
    const localSignals = readTextIfExists(paths.localSignalsPath);
    const seedInsights = isInsightsContentPopulated(localInsights)
      ? normalizeInsights(localInsights)
      : DEFAULT_INSIGHTS_TEMPLATE;
    const seedSignals = isSignalsContentPopulated(localSignals)
      ? normalizeSignals(localSignals, warnings)
      : "";

    writeTextAtomic(paths.backendInsightsPath, seedInsights);
    writeTextAtomic(paths.backendSignalsPath, seedSignals);
  });
}

function warnIfLocalMirrorDiffers(paths, warnings) {
  if (!isBackendPopulated(paths) || !isLocalPopulated(paths)) return;

  const backendInsights = normalizeInsights(readTextIfExists(paths.backendInsightsPath));
  const backendSignals = normalizeSignals(readTextIfExists(paths.backendSignalsPath));
  const localInsights = normalizeInsights(readTextIfExists(paths.localInsightsPath));
  const localSignals = normalizeSignals(readTextIfExists(paths.localSignalsPath));

  if (backendInsights !== localInsights || backendSignals !== localSignals) {
    warnings.push("local insight files differ from populated backend; backend remains authoritative");
  }
}

async function syncSessionStart(input) {
  const warnings = [];
  let repoContext;

  try {
    repoContext = repoHelpers.resolveRepoContext(input.cwd);
  } catch {
    return warnings;
  }

  const paths = getSyncPaths(repoContext);
  const backendPopulatedBefore = isBackendPopulated(paths);
  await ensureBackendInitialized(paths, input, warnings);

  if (backendPopulatedBefore) {
    warnIfLocalMirrorDiffers(paths, warnings);
  }

  const backendInsights = readTextIfExists(paths.backendInsightsPath) || DEFAULT_INSIGHTS_TEMPLATE;
  const backendSignals = readTextIfExists(paths.backendSignalsPath) || "";
  ensureLocalMirror(paths, backendInsights, backendSignals);
  return warnings;
}

async function syncStop(input) {
  const warnings = [];
  let repoContext;

  try {
    repoContext = repoHelpers.resolveRepoContext(input.cwd);
  } catch {
    return warnings;
  }

  const paths = getSyncPaths(repoContext);
  await ensureBackendInitialized(paths, input, warnings);

  await withLock(paths.lockPath, input, warnings, async () => {
    ensureSyncRepoJson(paths.repoContext);
    const backendInsights = readTextIfExists(paths.backendInsightsPath) || DEFAULT_INSIGHTS_TEMPLATE;
    const backendSignals = readTextIfExists(paths.backendSignalsPath) || "";
    const localInsights = readTextIfExists(paths.localInsightsPath) || DEFAULT_INSIGHTS_TEMPLATE;
    const localSignals = readTextIfExists(paths.localSignalsPath) || "";

    const mergedInsights = mergeInsightsDocuments(backendInsights, localInsights, warnings);
    const mergedSignals = mergeSignals(backendSignals, localSignals, warnings);

    writeTextAtomic(paths.backendInsightsPath, mergedInsights);
    writeTextAtomic(paths.backendSignalsPath, mergedSignals);
    ensureLocalMirror(paths, mergedInsights, mergedSignals);
  });

  return [...warnings];
}

async function runHook(input) {
  if (input.hookEventName === "SessionStart") {
    return syncSessionStart(input);
  }

  if (input.hookEventName === "Stop") {
    return syncStop(input);
  }

  return [];
}

async function main() {
  const input = normalizeInput(readHookInput());
  const warnings = await runHook(input);
  emitWarnings(warnings);
  process.stdout.write("{}");
}

if (require.main === module) {
  main().catch((err) => {
    process.stderr.write(`[tsp-insights-sync] ${err.message}\n`);
    process.stdout.write("{}");
  });
}

module.exports = {
  DEFAULT_INSIGHTS_TEMPLATE,
  LOCK_RETRY_COUNT,
  LOCK_RETRY_DELAY_MS,
  LOCK_STALE_MS,
  readHookInput,
  normalizeInput,
  parseInsightsDocument,
  renderInsightsDocument,
  mergeInsightsDocuments,
  parseSignals,
  mergeSignals,
  buildLockPayload,
  parseLockFile,
  isLockStale,
  acquireLock,
  releaseLock,
  getSyncPaths,
  isBackendPopulated,
  isLocalPopulated,
  syncSessionStart,
  syncStop,
  runHook,
};