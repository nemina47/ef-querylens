# Query Extraction V2 Slice 4

## Overview

Implement the final slice of query extraction v2.0 by cutting over all query extraction/runtime flows to v2, removing the legacy extraction/capture/runtime paths, and validating end-to-end behavior with deterministic diagnostics and stable SQL output for supported shapes.

## Scope Boundaries

**Feature context**: Slice 4 of Query Extraction V2

**This slice delivers**:

- Full runtime and LSP cutover to v2 extraction + capture + runtime contract paths
- Removal of legacy extraction/replay/compile-healing branches replaced by v2 behavior
- Final request/adapter cleanup so only one production path remains
- End-to-end validation artifacts proving supported-shape behavior and unsupported-shape diagnostics
- Documentation/state updates reflecting completion of v2 migration

**Out of scope / deferred**:

- New feature expansion beyond accepted v2 supported-shape contract
- Additional helper-category support not already approved in slices 1-3
- Backward-compatibility shims for deprecated legacy behaviors

**Depends on**:

- `query-extraction-v2-q7`
- `query-extraction-v2-capture-h2`
- `query-extraction-v2-runtime-m6`

**Expected implementation order**:

1. `query-extraction-v2-q7`
2. `query-extraction-v2-capture-h2`
3. `query-extraction-v2-runtime-m6`
4. `query-extraction-v2-cutover-r4`

## Requirements

- [ ] Remove legacy extraction and replay branches superseded by v2 in LSP parsing and capture flow
- [ ] Remove legacy runtime adapter/fallback branches that are no longer needed post-cutover
- [ ] Ensure all production query extraction requests are emitted in v2 contract form
- [ ] Keep unsupported-shape diagnostics explicit and deterministic after legacy fallback removal
- [ ] Validate supported query shapes (direct chains, query expressions, approved helper inlining patterns, approved multi-expression helper patterns) end-to-end
- [ ] Validate rejected/unsupported shape behavior end-to-end with stable diagnostics
- [ ] Update project docs/feature tracking files to reflect completed v2 rollout
- [ ] Leave the codebase with a single query extraction pipeline and no ambiguous dual-path behavior

## Design Decisions

### Decision 1: Hard cutover after slice 3 validation

**Choice:** Once slice 3 runtime behavior is validated, cut over fully and remove dual-path execution.

**Rationale:** Maintaining both legacy and v2 code paths keeps complexity high, increases regression surface, and undermines the goal of deterministic behavior.

**Alternatives considered:**

- Keep long-term dual-path support — rejected due to maintenance burden and behavior ambiguity
- Keep silent fallback to legacy for unsupported shapes — rejected because v2 requires explicit diagnostics and clear boundaries

### Decision 2: Unsupported shapes remain explicit failures

**Choice:** Post-cutover, unsupported shapes must continue to fail with clear extraction/runtime diagnostics rather than fallback execution.

**Rationale:** This preserves the contract established in slices 1-3 and prevents hidden behavior drift.

**Alternatives considered:**

- Reintroduce permissive fallback in cutover — rejected because it recreates legacy uncertainty

### Decision 3: Clean-up is part of delivery, not optional follow-up

**Choice:** Remove obsolete contracts, adapters, and helper branches during this slice.

**Rationale:** Deferring cleanup leaves latent coupling that can regress future changes and make reviews harder.

**Alternatives considered:**

- Separate cleanup into a fifth slice — rejected to avoid half-migrated steady state

## Implementation Plan

1. Inventory all remaining legacy query extraction/capture/runtime entry points and map each to v2 replacement.
2. Switch final production call paths to v2-only request emission and v2 runtime handling.
3. Remove legacy extraction and replay logic no longer used after v2 cutover.
4. Remove temporary adapters/fallbacks introduced for staged migration in slices 1-3.
5. Run targeted and full tests to verify supported-shape success and unsupported-shape diagnostics.
6. Validate SQL output on representative scenarios using harness-backed checks.
7. Update `features.md`, `impldocs/INDEX.md`, `roadmap.md`, `todos.md`, and `end-to-end-testing.md` as part of completion workflow.
8. Produce final change-log entries summarizing v2 rollout completion and remaining known limits.

## Dependencies

- Completed implementation outputs from slices 1-3
- Runtime and LSP code under `src/EFQueryLens.Lsp/Parsing` and `src/EFQueryLens.Core/Scripting`
- Test suites under `tests/EFQueryLens.Core.Tests`
- Query harness under `tools/EfQueryHarness/`

## Testing Strategy

### Unit Tests

- Assert no legacy path selection remains for extraction/capture/runtime request processing
- Assert deterministic diagnostics for unsupported shapes after fallback removal

### Integration Tests

- End-to-end hover-to-SQL flow for all supported shape categories
- End-to-end hover behavior for unsupported/rejected shapes with stable diagnostics

### Manual Smoke Tests

Steps for `end-to-end-testing.md`:

1. Hover supported direct chain and helper-composed chain examples — expected result: SQL shown via v2-only path
2. Hover supported query-expression examples — expected result: SQL shown via v2-only path
3. Hover unsupported branching/procedural helper shape — expected result: explicit diagnostic with no legacy fallback behavior

## Acceptance Criteria

- [ ] Production flow is v2-only for extraction, capture, and runtime evaluation
- [ ] Legacy query extraction/capture/runtime branches are removed
- [ ] Supported-shape scenarios pass end-to-end tests and smoke checks
- [ ] Unsupported-shape scenarios return explicit, deterministic diagnostics
- [ ] Migration adapters/fallbacks from slices 1-3 are removed
- [ ] Project documentation and impldoc statuses are updated as part of closure

## Review Findings

_Populated by the implementer after code review, security review, and quality analysis. Only findings that resulted in code changes are recorded here. Deferred items go to `todos.md`. Valid sources: `code-reviewer`, `red-team`, `query-analyzer`, `trivy`, `sonarqube`._

| Source | Finding | Resolution |
| --- | --- | --- |

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
| 2026-04-04 | Initial impldoc drafted | Planned final v2 cutover slice to remove legacy query extraction/runtime paths and complete rollout. |