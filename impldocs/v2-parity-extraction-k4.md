# V2 Accuracy-First Extraction and Execution

## Overview

Deliver an accuracy-first v2 extraction and runtime path designed to maximize semantic fidelity
from hovered LINQ source to executed query translation. This plan prioritizes correctness of
extraction and execution over delivery speed and avoids backward-support constraints.

## Scope Boundaries

**Feature context**: Full feature, no phasing needed (developer-selected)

**This slice delivers**:

- Semantic-model-driven extraction that preserves full query shape (roots, helper composition, capture graph, and materialization boundary)
- Accuracy-first capture binding that resolves closure/local/parameter/member captures with precise typing and deterministic declaration order
- Runtime execution path that emits query stubs from resolved captures without compatibility fallbacks
- Structured unresolved-capture diagnostics and logging taxonomy to explain why capture binding failed and which code shape caused it
- Accuracy validation matrix covering supported query families across representative provider behaviors
- Deterministic diagnostics when extraction or binding cannot be made semantically correct

**Out of scope / deferred**:

- IDE UI enhancements unrelated to extraction/runtime correctness
- Performance tuning and cache optimization not required for correctness
- New user settings and toggles

**Depends on**: `v2-production-wiring-p9` must be complete first

## Requirements

- [ ] Extraction reconstructs executable query shape with semantically correct boundaries, helper composition, and context variable lineage
- [ ] Capture binding resolves closure/local/parameter/member values with accurate type information and deterministic declaration ordering
- [ ] Runtime stub generation preserves predicate semantics for string, scalar, nullable, and collection captures used in translatable query patterns
- [ ] Predetermined default catalog is implemented for core types first (string, bool, numeric primitives, decimal, DateTime, DateOnly, TimeOnly, Guid, enums, and nullable variants)
- [ ] For non-catalog custom/reference types, default values are inferred from expected expression type shape where possible; otherwise deterministic typed defaults are synthesized
- [ ] Collection defaults (arrays, lists, and set-like enumerable shapes) are seeded with at least two deterministic items to preserve operator behavior (`Any`, `All`, `Contains`, indexing, and count-sensitive predicates)
- [ ] No backward-support fallback is added; unsupported shapes return explicit deterministic diagnostics
- [ ] Unresolved capture diagnostics are logged with normalized category, symbol path, and failure reason to support developer debugging and roadmap prioritization
- [ ] Procedural helper bodies (branches/loops/local mutations) are supported via symbolic evaluation in extraction/capture planning
- [ ] Accuracy matrix tests pass for all in-scope query families and provider combinations
- [ ] End-to-end execution tests confirm the produced SQL preserves predicate intent and does not collapse due to capture placeholder artifacts

## Design Decisions

### Decision 1: Accuracy Is Primary Optimization Target

**Choice:** Optimize extraction and execution for semantic correctness first, even when implementation complexity increases.

**Rationale:** The feature goal is trustworthy SQL preview behavior for developers. Correctness failures are more harmful than additional implementation effort.

**Alternatives considered:**

- Optimize for delivery speed with partial accuracy coverage — rejected because it leaves developer-facing correctness gaps
- Add permissive heuristics that degrade semantic fidelity in ambiguous cases — rejected because it hides extraction defects

### Decision 2: No Backward-Support Fallback Path

**Choice:** Do not introduce compatibility fallback paths for extraction/runtime failures.

**Rationale:** Fallback behavior masks accuracy defects and prevents deterministic improvement of the core extraction/runtime engine.

**Alternatives considered:**

- Legacy replay fallback for unsupported v2 shapes — rejected by product direction
- Feature flag to toggle between accurate and permissive modes — rejected to avoid ambiguous behavior contracts

### Decision 3: Accuracy Matrix as Hard Merge Gate

**Choice:** Introduce an accuracy matrix test suite as a required quality gate for merge.

**Rationale:** Repeatable, executable validation is required to ensure extraction/runtime behavior remains accurate as coverage expands.

**Alternatives considered:**

- Ad hoc spot checks only — rejected because they are insufficiently reliable for correctness-critical behavior
- Unit-only validation without end-to-end query execution checks — rejected because it misses pipeline-level semantic drift

### Decision 4: Unresolved Captures Must Be Taxonomized and Logged

**Choice:** Any unresolved capture value must produce structured diagnostics and logging with a normalized category and reason.

**Rationale:** Accuracy work requires visibility into failure shapes. Without structured unresolved telemetry, we cannot reliably improve extractor/binder coverage.

**Alternatives considered:**

- Generic unresolved errors without shape taxonomy — rejected because they are not actionable
- Silent unresolved fallback behavior — rejected by product direction

### Decision 5: Procedural Helpers Are In Scope Now

**Choice:** Implement symbolic evaluation support for procedural helper bodies in this feature.

**Rationale:** Developers routinely compose queries through helper methods with local control flow. Rejecting these patterns undermines real-world usefulness of the tool.

**Alternatives considered:**

- Defer procedural helper support to a later slice — rejected by developer direction
- Require manual helper rewrites for supported extraction — rejected as poor developer experience

### Decision 6: Validation Uses Provider-Aware Semantic Equivalence

**Choice:** Use semantic SQL equivalence (provider-aware) as the correctness contract, not exact SQL text snapshots.

**Rationale:** SQL text formatting and provider query generation differences can vary while preserving identical predicate semantics.

**Alternatives considered:**

- Exact SQL string snapshot matching as primary gate — rejected because it is brittle and can fail on non-semantic differences

### Decision 7: Core-Type Catalog First, Then Type-Inferred Defaults

**Choice:** Implement a predetermined default catalog for core CLR types first; for all other types, attempt expected-type-driven default synthesis.

**Rationale:** Core types cover the majority of query captures and can be made deterministic quickly. Type-inferred fallback keeps coverage broad without blocking on full custom-type modeling.

**Alternatives considered:**

- Full universal catalog before shipping — rejected as high effort with lower near-term coverage gain
- Core-only catalog with hard reject for all other types — rejected because it leaves avoidable unresolved cases

### Decision 8: Collection Defaults Must Include At Least Two Items

**Choice:** When synthesizing default collections (arrays/lists/enumerables), include at least two deterministic items.

**Rationale:** Single-item or empty collections can distort translation behavior for membership and cardinality-sensitive query operators.

**Alternatives considered:**

- Empty collection defaults — rejected because they can collapse predicates to constant-false/constant-true forms
- Single-item collection defaults — rejected because they underrepresent common query operator behavior

## Implementation Plan

1. Define the accuracy contract for extraction and execution: root detection, boundary semantics, helper composition rules, and capture binding correctness.
2. Implement semantic-model-driven extraction normalization for query roots, helper chains, and executable boundary reconstruction.
3. Implement capture binder upgrades for locals, closure members, parameters, and member-access paths with deterministic initialization ordering.
4. Add unresolved-capture taxonomy and logging pipeline (diagnostic code, category, symbol path, source location, reason, and suggested remediation).
5. Implement symbolic evaluation for procedural helper bodies (branching, loop-carried locals, and local mutations) where resulting expressions remain translatable.
6. Build the predetermined default catalog for core CLR capture types and nullable variants.
7. Implement expected-type-driven fallback synthesis for non-catalog types.
8. Extend runtime stub generation to preserve translatable semantics for string, scalar, nullable, and collection captures, including two-item deterministic collection seeding.
9. Add end-to-end execution coverage for representative query families, including `Contains`, `StartsWith`, comparison predicates, projections, ordering, materialization boundaries, and procedural helper composition.
10. Add deterministic rejection coverage for unsupported/unsafe shapes with clear actionable diagnostics and taxonomy tags.
11. Build and run an accuracy matrix suite across selected providers and verify semantic fidelity of produced SQL.
12. Record any remaining correctness gaps as explicit follow-up items with diagnostics and failing test evidence.

## Dependencies

- `v2-production-wiring-p9` (in-progress): establishes production v2 stub-path wiring
- Query extraction modules under `src/EFQueryLens.Lsp/Parsing`
- Runtime synthesis/evaluation modules under `src/EFQueryLens.Core/Scripting`
- Existing query harness at `tools/EfQueryHarness/` for SQL-shape verification support

## Testing Strategy

### Unit Tests

- Extraction normalization behavior for helper composition and boundary detection
- Capture binding behavior for closure, local, parameter, and member-access sources
- Unresolved-capture diagnostic taxonomy mapping for each supported/unsupported capture shape
- Runtime placeholder and initialization semantics for string, scalar, nullable, and collection captures
- Core-type default catalog tests with deterministic emitted values
- Type-inferred fallback default synthesis tests for non-catalog types
- Collection synthesis tests asserting minimum two seeded items and stable ordering
- Symbolic evaluation primitives for procedural helper control-flow reconstruction

### Integration Tests

- Accuracy matrix tests covering in-scope query families and provider variants
- End-to-end query execution tests validating semantic predicate fidelity in produced SQL
- Collection-sensitive execution tests (`Contains`, `Any`, `All`, count/index-sensitive patterns) against seeded defaults
- Negative-path tests proving deterministic diagnostics for unsupported shapes without fallback
- Logging contract tests that validate unresolved category/reason emission

### Manual Smoke Tests

Steps for `end-to-end-testing.md`:

1. Hover a query using captured string local in `Contains` and `StartsWith` predicate — expected result: translated SQL preserves predicate intent and does not collapse to constant-false conditions
2. Hover a query using closure-backed scalar and nullable predicates in `Where` — expected result: translated SQL preserves comparison semantics
3. Hover a helper-composed query with projection and ordering — expected result: translated SQL preserves composition order and filter semantics
4. Hover a procedural helper with branching and loop-derived predicates — expected result: translated SQL preserves predicate intent
5. Hover an unsupported procedural helper shape — expected result: explicit deterministic diagnostic with unresolved category and reason, no fallback SQL result

## Acceptance Criteria

- [ ] Extraction output is semantically accurate for all in-scope query families defined by the accuracy contract
- [ ] Capture binding and runtime initialization preserve predicate semantics for all in-scope capture types
- [ ] Core-type default catalog is implemented and used by runtime initialization code
- [ ] Non-catalog type default synthesis uses expected-type inference before unresolved rejection
- [ ] Collection defaults always seed at least two deterministic items and pass collection-sensitive query tests
- [ ] Unresolved capture logging emits normalized categories and actionable reasons for all rejection paths
- [ ] Procedural helper symbolic evaluation passes end-to-end correctness tests for in-scope control-flow patterns
- [ ] Accuracy matrix suite passes across selected provider combinations
- [ ] Unsupported/unsafe shapes produce deterministic diagnostics with no fallback behavior
- [ ] End-to-end tests are added and passing for all covered query families
- [ ] Any remaining correctness gaps are documented with explicit failing cases and planned remediation

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
| 2026-04-04 | Implemented v2 diagnostic infrastructure: extended V2CaptureDiagnostic contract with full schema (category, reason, symbolPath, sourceLocation, suggestedFix, provider); created EvalSourceBuilderDiagnostics factory (10 unresolved categories); wired emission into placeholder generation for unsupported types; added 11 unit tests; updated LSP capture-plan-builder diagnostics to include categorization; test results 705 passing, 0 failures | Enable structured measurement and logging of capture failures per taxonomy from docs/unresolved-capture-examples.md; unlocks data-driven future optimizations (operator-awareness, semantic rewrite strategies). |
| 2026-04-04 | Updated date placeholder defaults to `DateTime.UtcNow`, `DateOnly.FromDateTime(DateTime.UtcNow)`, and `TimeOnly.FromDateTime(DateTime.UtcNow)`; aligned tests/docs | Developer requested `Now`/`Today` style date defaults instead of fixed historical baselines. |
| 2026-04-04 | Captured provider behavior in integration coverage: list-based `Contains` placeholder succeeds while array/IEnumerable shapes currently return deterministic parameter-evaluation failure in runtime pipeline tests | Preserve truthful behavior coverage while keeping array/IEnumerable two-item seeding validated at unit level. |
| 2026-04-04 | Added collection-sensitive v2 integration tests for array/list/enumerable capture placeholders and hash-set placeholder unit coverage | Validate two-item deterministic seeding preserves SQL translation shape for collection operators (`Contains`, `IN`) in runtime pipeline. |
| 2026-04-04 | Implemented v2 placeholder default catalog core: canonical scalar defaults plus two-item deterministic seeds for arrays/lists/enumerables/set-like collections; added focused unit coverage | Execute approved accuracy plan with practical high-coverage variable synthesis and collection fidelity for query operators. |
| 2026-04-04 | Added default-value policy: core-type catalog first, inferred defaults for non-catalog types, and minimum two-item collection seeds | Developer requested pragmatic high-coverage default synthesis strategy with collection behavior fidelity. |
| 2026-04-04 | Accepted architecture choices: unresolved-capture taxonomy logging, procedural helper symbolic evaluation now, and provider-aware semantic SQL equivalence contract | Developer selected option 1 (log unresolved categories/reasons), option 2A, and option 3A to maximize real-world extraction/runtime accuracy. |
| 2026-04-04 | Refocused spec to accuracy-first execution with no backward-support fallback constraints | Developer clarified that correctness and developer effectiveness are the primary goal; implementation speed and compatibility backsupport are not optimization targets. |
| 2026-04-04 | Initial impldoc drafted | Defines parity-first, no-fallback Slice 1 to eliminate legacy-v2 synthesis drift and enforce overlap parity with matrix tests before cutover. |