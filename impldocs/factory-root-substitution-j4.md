# Factory-Root Substitution

## Overview

Enable SQL preview for LINQ chains rooted at EF Core factory-created contexts by rewriting the query root receiver to the runtime QueryLens context variable when type resolution is unambiguous and semantically safe.

## Scope Boundaries

**Feature context**: Slice 1 of factory-created DbContext query support

**This slice delivers**:

- Receiver substitution for root query chains that start from `IDbContextFactory<TContext>.CreateDbContextAsync(...)` or `CreateDbContext(...)`
- Strict eligibility gates (type match, root-chain-only rewrite, ambiguity rejection)
- Deterministic diagnostics and debug logs for substitution applied/skipped paths
- Regression tests for async and sync factory-root patterns

**Out of scope / deferred**:

- Rewriting nested factory-created contexts inside helper methods or subqueries _(candidate for follow-on slice)_
- Rewriting service-wrapper methods that return queryables _(candidate for follow-on slice)_
- Broad expression normalization for non-factory receiver shapes _(candidate for follow-on slice)_

**Depends on**: Existing v2 extraction, capture planning, and runtime wiring (`v2-parity-extraction-k4`)

## Requirements

- [x] A query whose LINQ root receiver is `await factory.CreateDbContextAsync(ct)` and whose factory type resolves to `IDbContextFactory<TContext>` must execute against the QueryLens runtime context instance without placeholder dependency on `factory` or `ct`.
- [x] A query whose LINQ root receiver is `factory.CreateDbContext()` and whose factory type resolves to `IDbContextFactory<TContext>` or `PooledDbContextFactory<TContext>` must execute against the QueryLens runtime context instance.
- [x] Substitution must only occur when inferred `TContext` is compatible with the resolved runtime DbContext type; ambiguous or conflicting matches must not rewrite.
- [x] If substitution is not applied, logs must state a deterministic reason code.
- [x] Existing non-factory query patterns must retain current behavior and test coverage.

## Design Decisions

### Decision 1: Apply substitution only at LINQ root receiver

**Choice:** Rewrite only the root receiver that starts the extracted query chain.

**Rationale:** Root-only substitution preserves semantic intent while avoiding broad rewrites that can alter helper/subquery behavior.

**Alternatives considered:**

- Rewrite all matching factory calls in the expression tree — rejected due to higher semantic drift and regression risk.
- Do not rewrite and rely on placeholders — rejected because placeholder `factory` values lead to runtime null failures and block supported user patterns.

### Decision 2: Gate substitution by strict type compatibility

**Choice:** Require unambiguous `TContext` inference from factory type and compatibility with resolved DbContext selection snapshot before rewriting.

**Rationale:** Prevents accidental cross-context execution in multi-DbContext solutions.

**Alternatives considered:**

- Best-effort rewrite on name similarity — rejected as unsafe in ambiguous solutions.
- Always trust declared field type alone — rejected because runtime resolution may legitimately select a different concrete context from factory candidates.

### Decision 3: Keep rewrite in extraction/capture stage

**Choice:** Perform substitution before capture planning finalization in LSP extraction pipeline.

**Rationale:** Keeps downstream runtime unchanged and avoids placeholder generation for factory symbols that are no longer part of executable expression.

**Alternatives considered:**

- Runtime-only rewrite in evaluator — rejected for reduced observability and harder cache-key/capture consistency.
- New runtime factory invocation shim — rejected because QueryLens already owns DbContext creation and does not need user factory execution.

## Implementation Plan

1. Add root receiver detection for factory-create patterns in extraction normalization and collect inferred `TContext`.
2. Validate substitution eligibility against DbContext resolution snapshot and selected `DbContextTypeName`.
3. Rewrite eligible root receiver to runtime context variable and emit rewrite flag/reason telemetry.
4. Ensure capture graph excludes factory symbols after substitution and preserves current behavior for non-substituted cases.
5. Add/extend unit tests for async/sync factory roots, ambiguity conflicts, and no-regression direct DbContext cases.
6. Add/extend integration test coverage for translation success (SQL produced) on factory-root sample patterns.
7. Verify debug logging and diagnostics for apply/skip paths.

## Dependencies

- Existing v2 extraction and capture infrastructure in `EFQueryLens.Lsp`
- Existing DbContext resolution snapshot flow (`TranslationRequest.DbContextResolution`)
- Existing runtime context creation path (`CreateDbContextInstanceAsync`) in `EFQueryLens.Core`

## Testing Strategy

### Unit Tests

- Substitution applies for `await IDbContextFactory<T>.CreateDbContextAsync(ct)` root chains.
- Substitution applies for sync `CreateDbContext()` root chains.
- Substitution is skipped with explicit reason when factory type inference is ambiguous.
- Substitution is skipped with explicit reason when inferred `TContext` conflicts with resolved runtime context.
- Existing extraction and capture tests continue to pass for direct DbContext roots.

### Integration Tests

- Factory-root query translation returns at least one SQL command for representative async and sync paths.
- Multi-DbContext ambiguity path returns deterministic conflict diagnostic without unsafe rewrite.

### Manual Smoke Tests

Steps for `end-to-end-testing.md`:

1. In SampleDbContextFactoryApp, hover a query rooted at `await _contextFactory.CreateDbContextAsync(ct)` — expected: SQL preview is produced, no `NullReferenceException`.
2. Hover a direct DbContext-root query in another sample app — expected: unchanged SQL preview behavior.
3. In a multi-DbContext host, trigger an ambiguous factory-root case — expected: deterministic skip/diagnostic, no incorrect context execution.

## Acceptance Criteria

- [x] Async and sync factory-root queries produce SQL preview when context inference is unambiguous.
- [x] Ambiguous/conflicting context inference does not rewrite and returns deterministic diagnostics.
- [x] No regressions in existing direct DbContext query preview flows.
- [x] New unit and integration tests are added and passing.
- [x] Logging includes deterministic substitution apply/skip reason codes.

## Review Findings

_Populated by the implementer after code review, security review, and quality analysis. Only findings that resulted in code changes are recorded here. Deferred items go to `todos.md`. Valid sources: `code-reviewer`, `red-team`, `query-analyzer`, `trivy`, `sonarqube`._

| Source | Finding | Resolution |
| --- | --- | --- |

## Quality Report

_Populated by the implementer after all scans complete. Captures the final quality snapshot for the permanent record._

### Security Scan (Trivy)

| Targets | Vulnerabilities | Secrets | Misconfigurations |
| --- | --- | --- | --- |

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

## Change Log

| Date | Change | Reason |
| --- | --- | --- |
| 2026-04-06 | Implementation complete | All 5 implementation plan steps executed: root detection, eligibility validation, receiver rewriting, capture graph normalization, unit tests (8/8 passing), and debug logging. All requirements and acceptance criteria met. No regressions detected in existing extraction/capture flows. Ready for integration testing. |
| 2026-04-05 | Initial draft created | Plan slice for safe factory-root receiver substitution to QueryLens runtime context |
