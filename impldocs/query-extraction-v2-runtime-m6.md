# Query Extraction V2 Slice 3

## Overview

Implement the third slice of query extraction v2.0 by rewriting the daemon-facing request contract and runtime/code generation path to consume the new extraction IR and capture plan. This slice removes dependence on legacy compile-retry/stub-healing behavior for supported v2 shapes while preserving a controlled compatibility bridge until final cutover.

## Scope Boundaries

**Feature context**: Slice 3 of Query Extraction V2

**This slice delivers**:

- New request contract fields for v2 extraction payload and capture-plan semantics
- Runtime pipeline updates to compile and execute v2 query payloads deterministically
- Runner template/codegen updates aligned to v2 capture decisions
- Explicit runtime diagnostics for unsupported or rejected capture/extraction shapes
- Adapter bridge so slice 4 can cut over and remove legacy path cleanly

**Out of scope / deferred**:

- Final cutover, legacy path removal, and full behavior switch-over _(planned for impldoc `query-extraction-v2-cutover-??`)_
- Additional helper support beyond slice 1 and slice 2 accepted contracts
- Broad runtime fallback heuristics for unsupported v2 shapes

**Depends on**:

- `query-extraction-v2-q7`
- `query-extraction-v2-capture-h2`

**Expected implementation order**:

1. `query-extraction-v2-q7`
2. `query-extraction-v2-capture-h2`
3. `query-extraction-v2-runtime-m6`
4. `query-extraction-v2-cutover-??`

## Requirements

- [x] Add v2 request contract fields and versioning to carry extraction IR and capture plan to runtime
- [x] Implement runtime request validation for v2 payload integrity and compatibility boundaries
- [ ] Update eval source builder and runner generation to consume capture-plan directives (replay, placeholder, reject) - _Deferred to Slice 3b_
- [ ] Remove reliance on legacy runtime healing paths for supported v2 scenarios - _Deferred to Slice 3b_
- [x] Provide clear runtime error diagnostics for rejected or unsupported payload shapes
- [x] Maintain temporary adapter support so existing call sites can still execute while cutover is pending
- [x] Add tests covering v2 contract validation, runtime adapter decision logic, and execution outcomes
- [ ] Verify SQL output parity for supported v2 scenarios using harness-backed checks - _Deferred to Slice 3b_

## Design Decisions

### Decision 1: Runtime contract versioning is mandatory in slice 3

**Choice:** Introduce an explicit v2 payload contract shape and guard execution with strict contract checks.

**Rationale:** Slice 3 changes runtime assumptions about extraction and capture. Loose compatibility would hide mismatches and produce non-deterministic failures.

**Alternatives considered:**

- Reuse current request contract without explicit v2 fields — rejected because semantics become ambiguous
- Delay contract changes until cutover slice — rejected because runtime rewrite needs stable payload now

### Decision 2: Deterministic runtime over compile-time healing

**Choice:** For supported v2 shapes, runtime should execute deterministic codegen paths and avoid legacy compile-retry heuristics as primary behavior.

**Rationale:** The v2 effort is intended to reduce fragile recovery logic and improve predictability.

**Alternatives considered:**

- Keep legacy healing as first-class behavior — rejected because it preserves current unpredictability
- Hard remove all legacy paths in slice 3 — rejected because final migration/cutover belongs to slice 4

### Decision 3: Keep a narrow adapter bridge until slice 4

**Choice:** Retain a temporary adapter from old request producers into v2 runtime boundary where practical.

**Rationale:** Enables incremental rollout and testing without coupling slice 3 completion to full cutover.

**Alternatives considered:**

- Immediate hard cutover in slice 3 — rejected because it combines runtime rewrite and migration risk in one slice
- No adapter support at all — rejected because it blocks staged verification

## Implementation Plan

1. Extend contracts to introduce v2 payload and capture-plan transport fields with explicit version semantics.
2. Update request normalization/validation in evaluator pipeline to accept v2 payloads and reject invalid combinations.
3. Update eval source and runner generation to emit code based on capture-plan classifications.
4. Update runtime invocation and diagnostics paths to report deterministic errors for rejected/unsupported shapes.
5. Gate or bypass legacy compile-healing paths when v2 payload is present and supported.
6. Add adapter bridge paths needed to run existing call sites during pre-cutover stage.
7. Add unit and integration tests for contract validation, runner output, and execution behavior.
8. Validate SQL generation for supported scenarios using EF query harness and targeted runtime tests.

## Dependencies

- V2 extraction and helper inlining output from `query-extraction-v2-q7`
- V2 capture-plan model and policy output from `query-extraction-v2-capture-h2`
- Runtime/evaluator code under `src/EFQueryLens.Core/Scripting`
- Contract types under `src/EFQueryLens.Core/Contracts`
- Test suites under `tests/EFQueryLens.Core.Tests/Scripting`

## Testing Strategy

### Unit Tests

- Validate contract version and payload validation behavior
- Validate capture-plan-driven code generation for replay/placeholder/reject classifications
- Validate deterministic runtime diagnostics for rejected payloads

### Integration Tests

- Execute v2 payloads through evaluator pipeline and assert SQL/non-SQL outcomes
- Verify adapter bridge behavior for transitional request producers

### Manual Smoke Tests

Steps for `end-to-end-testing.md`:

1. Execute a supported v2 direct query chain payload — expected result: runtime compiles and returns SQL with deterministic behavior
2. Execute a helper-composed v2 payload with multiple expression captures — expected result: runtime compiles with correct capture handling and returns SQL
3. Execute a rejected v2 payload shape — expected result: clear runtime diagnostic, no silent legacy healing

## Acceptance Criteria

- [x] V2 runtime contract fields and validation are implemented
- [ ] Runtime codegen consumes capture-plan classifications directly - _Deferred to Slice 3b_
- [ ] Supported v2 scenarios execute without relying on legacy healing as primary path - _Deferred to Slice 3b_
- [x] Rejected/unsupported v2 payloads produce explicit diagnostics
- [x] Adapter bridge works for pre-cutover transitional flows
- [x] Runtime tests pass for v2 contract validation and decision logic

## Review Findings

_Populated by the implementer after code review, security review, and quality analysis. Only findings that resulted in code changes are recorded here. Deferred items go to `todos.md`. Valid sources: `code-reviewer`, `red-team`, `query-analyzer`, `trivy`, `sonarqube`._

| Source | Finding | Resolution |
| --- | --- | --- |
| internal | V2RuntimeAnalyzer tests created to validate decision logic | 10 unit tests added, all passing (no-payloads, incomplete-payloads, capture-diagnostics, complete-v2-path, capture-policy-classification, diagnostic-formatting) |
| internal | V2 contract integration in LSP hover pipeline | LSP pipeline calls TryBuildV2ExtractionPlan(), maps result to V2QueryExtractionPlanSnapshot in TranslationRequest |
| internal | Daemon validation of v2 extraction plan payloads | DaemonRuntime.ValidateSnapshotConsistency() extends validation rules, cache key includes v2 data for deduplication |

## Quality Report

_Populated by the implementer after all scans complete. Captures the final quality snapshot for the permanent record._

### Security Scan (Trivy)

| Targets | Vulnerabilities | Secrets | Misconfigurations |
| --- | --- | --- | --- |

Or: _"Security scan skipped — Trivy not installed."_

### Code Quality (SonarQube)

**Quality Gate**: _PASSED / FAILED / Skipped_

| Metric | Value | Threshold | Status |
| --- | --- | --- | --- |

#### Issues Summary

| Type | Count | Top Finding |
| --- | --- | --- |
| Bugs (reliability) | | |
| Vulnerabilities | | |
| Code Smells (maintainability) | | |
| Security Hotspots | | |

Or: _"Code quality analysis skipped — SonarQube not configured."_

## Change Log

| Date | Change | Reason |
| --- | --- | --- |
| 2026-04-04 | Slice 3 split into 3a (transport) and 3b (codegen) phases | Scope decomposition: 3a delivers contract/validation/adapter; 3b deferred for codegen/healing updates. |
| 2026-04-04 | Slice 3a implementation complete: contract transport layer + V2RuntimeAdapter foundation | Contracts added (V2QueryExtractionPlanSnapshot, V2ExtractionDiagnostic), LSP→daemon transport wired, daemon validation in place, cache key updated to include v2 data, V2RuntimeAdapter provides deterministic decision logic with classification helpers + diagnostic formatting. 5 Slice 1 + 4 Slice 2 + 10 V2RuntimeAnalyzer tests all passing. Ready for Slice 3b codegen integration. |