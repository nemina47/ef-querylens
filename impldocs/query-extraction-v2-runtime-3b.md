# Query Extraction V2 Slice 3b — Runtime Codegen Integration

## Overview

Implement Slice 3b by integrating V2RuntimeAdapter decision logic into the QueryEvaluator pipeline and updating EvalSourceBuilder/RunnerGenerator to consume v2 capture-plan directives. This slice converts v2 extraction and capture IR into deterministic code execution, removing reliance on legacy compile-retry healing for supported scenarios.

## Scope Boundaries

**Feature context**: Slice 3b of Query Extraction V2 (codegen and evaluation phase)

**This slice delivers**:

- QueryEvaluator integration with V2RuntimeAdapter for deterministic path selection
- EvalSourceBuilder updates to interpret replay/placeholder/reject policies
- RunnerGenerator updates to emit v2-aware parameter initialization code
- Early return with structured diagnostics for rejected v2 payloads (no silent fallback)
- Integration tests for simple v2 scenarios (direct chains, helper composition, rejection cases)
- SQL parity validation via manual harness testing for supported shapes

**Out of scope / deferred**:

- Legacy compile-retry healing removal (belongs to Slice 4 cutover)
- Complex helper scenarios beyond Slice 1/2 acceptance scope
- Async/await pattern codegen refinement (future optimization)
- Harness automation layer (can be added post-launch)

**Depends on**:

- `query-extraction-v2-q7` ✓
- `query-extraction-v2-capture-h2` ✓
- `query-extraction-v2-runtime-m6` (Slice 3a) ✓

## Requirements

- [ ] Integrate V2RuntimeAdapter.Analyze() into QueryEvaluator.TranslateAsync() call path
- [ ] For blocked v2 payloads, return structured rejection diagnostic (no legacy fallback in this slice)
- [ ] Update EvalSourceBuilder.BuildReplayInitializerCode() to handle v2 capture-plan entries
- [ ] Update RunnerGenerator to emit initialization code for replay/placeholder/reject policies
- [ ] Add unit tests for EvalSourceBuilder v2 code generation
- [ ] Add unit tests for RunnerGenerator v2 initialization patterns
- [ ] Add integration tests covering: direct terminal chain, helper composition, invalid payload rejection
- [ ] Validate SQL output parity for supported v2 scenarios using query harness (manual testing)
- [ ] Ensure no regression in existing non-v2 query evaluation paths

## Design Decisions

### Decision 1: V2 Rejection Returns Structured Diagnostic, No Legacy Fallback

**Choice:** When V2RuntimeAdapter.Analyze() returns `ShouldUseV2Path: false`, QueryEvaluator returns a TranslationResult with explicit error diagnostic. No attempt to execute legacy healing path.

**Rationale:** 
- V2 payloads are intentionally crafted by Slice 1/2 extraction. Rejection means the payload is fundamentally incompatible (missing symbol, unsupported helper, dialect mismatch).
- Silent fallback hides bugs and makes debugging harder.
- Users get clear diagnostics about why v2 path failed.

**Alternatives considered**:
- Silent fallback to legacy path — rejected because masks issues; hard to debug
- Throw exception on rejection — rejected because TranslationResult error flow is established pattern

### Decision 2: Capture-Plan Policies Drive Code Emission

**Choice:** EvalSourceBuilder and RunnerGenerator consult capture-plan classification (ReplayInitializer/UsePlaceholder/Reject) to determine what code to emit. No heuristics; policies are deterministic.

**Rationale**:
- Slice 2 capture plan already made the decision about which replay policy to use.
- Codegen should just execute that decision, not re-decide.
- Makes testing deterministic and auditable.

**Alternatives considered**:
- Codegen re-analyzes and makes new decisions — rejected because duplicates Slice 2 work
- Mix heuristics with policy — rejected because introduces non-determinism

### Decision 3: Early Integration Tests, Manual SQL Validation

**Choice:** Add integration tests for happy path (direct chain, composed helper, rejection). Use query harness for manual spot-checks of SQL parity. Automated harness assertions deferred to future optimization.

**Rationale**:
- Integration tests confirm end-to-end flow works (LSP→daemon→codegen→execution).
- Manual harness testing is lightweight and sufficient for launch (limited v2 scenarios).
- Automated assertions can be added post-launch with harness enrichment.
- Keeps scope focused on correctness, not infrastructure.

**Alternatives considered**:
- Skip manual testing — rejected because SQL parity is critical
- Automate all harness tests now — rejected because harness skeleton still needs enrichment
- Only unit tests, no integration — rejected because need end-to-end validation

## Implementation Plan

1. Read QueryEvaluator.TranslateAsync() and identify where to inject V2RuntimeAdapter decision logic
2. Call V2RuntimeAdapter.Analyze(request) at entry to TranslateAsync()
3. If `ShouldUseV2Path: false`, construct TranslationResult.Failed with diagnostic reason and return early
4. If `ShouldUseV2Path: true`, proceed to v2 codegen paths (new steps below)
5. In EvalSourceBuilder.BuildReplayInitializerCode(): check if entry.CapturePolicy is ReplayInitializer, emit current replay code; if UsePlaceholder, emit placeholder; if Reject, skip/error
6. In RunnerGenerator.GenerateRunner(): update parameter initialization loop to check capture-plan policies and emit appropriate initialization
7. Add unit tests for each policy type in isolation (replay/placeholder/reject emissions)
8. Add integration test fixture: simple query → v2 extraction → v2 codegen → execution → assert SQL
9. Add integration test: composed helper → v2 capture → v2 codegen → assert initialization correct
10. Add integration test: invalid payload (e.g., missing symbol) → v2 rejection → assert diagnostic returned
11. Run query harness on sample v2 scenarios and compare SQL output with legacy generation (manual spot-check)
12. Run full test suite to confirm no regressions in existing non-v2 paths

## Dependencies

- V2RuntimeAdapter in `src/EFQueryLens.Core/Contracts/V2RuntimeAdapter.cs` (just created in 3a)
- QueryEvaluator in `src/EFQueryLens.Core/Scripting/QueryEvaluator.cs`
- EvalSourceBuilder in `src/EFQueryLens.Core/Scripting/EvalSourceBuilder.cs`
- RunnerGenerator in `src/EFQueryLens.Core/Scripting/RunnerGenerator.cs`
- V2 capture plan types from Slice 2
- Query harness in `tools/EfQueryHarness/`

## Testing Strategy

### Unit Tests

1. **EvalSourceBuilder V2 Tests** — new file `EvalSourceBuilderV2Tests.cs`
   - ReplayInitializer policy → emits replay init code
   - UsePlaceholder policy → emits placeholder init code
   - Reject policy → skips or errors appropriately
   - Mixed policies in same entry list → correct handling

2. **RunnerGenerator V2 Tests** — new file `RunnerGeneratorV2Tests.cs`
   - V2 parameter initialization codegen for each policy
   - Assert generated code compiles and runs
   - Verify placeholder values are contextually correct

### Integration Tests

1. **Direct Terminal Chain** — new file `QueryEvaluatorV2Tests.DirectChain.cs`
   - Query: `db.Users.Where(u => u.IsActive).ToListAsync()`
   - V2 extraction captures `u` as ReplayInitializer
   - Assert: codegen runs, SQL returned, no errors

2. **Composed Helper** — extend existing helpers test
   - Query using v2-approved helper with expression parameter
   - V2 capture plan has multiple entries with mixed policies
   - Assert: all entries initialized correctly, SQL matches expected shape

3. **Rejection Scenario** — new file `QueryEvaluatorV2Tests.Rejection.cs`
   - Payload with diagnostic (e.g., unsupported control flow in helper)
   - Assert: TranslateAsync returns Failed result
   - Assert: diagnostic message includes reason (e.g., "unsupported-helper-control-flow")

### Manual Smoke Tests

Steps for `end-to-end-testing.md`:

1. Run query harness on a v2-approved direct chain and legacy generation; compare SQL output
2. Run query harness on a v2 helper-composed query; verify SQL includes helper materializations where expected
3. Hover over a rejected v2 query (unsupported shape) and verify LSP error message is clear

## Acceptance Criteria

- [ ] V2RuntimeAdapter.Analyze() is called in QueryEvaluator.TranslateAsync()
- [ ] Blocked v2 payloads return structured diagnostic, no legacy fallback
- [ ] EvalSourceBuilder correctly interprets and emits code for replay/placeholder/reject policies
- [ ] RunnerGenerator correctly initializes v2 capture-plan entries
- [ ] All new unit tests pass (P0: policy code generation)
- [ ] All new integration tests pass (P0: end-to-end direct/helper/rejection flows)
- [ ] Manual harness spot-checks confirm SQL parity for supported v2 scenarios
- [ ] Existing non-v2 query paths have zero regression (P0: all pre-v2 tests still pass)

## Review Findings

_Populated by the implementer after code review, security review, and quality analysis._

| Source | Finding | Resolution |
| --- | --- | --- |

## Quality Report

_Populated by the implementer after all scans complete._

### Security Scan (Trivy)

| Targets | Vulnerabilities | Secrets | Misconfigurations |
| --- | --- | --- | --- |

Or: _"Security scan skipped — Trivy not installed."_

### Code Quality (SonarQube)

**Quality Gate**: _PASSED / FAILED / Skipped_

| Metric | Value | Threshold | Status |
| --- | --- | --- | --- |

Or: _"Code quality analysis skipped — SonarQube not configured."_

## Change Log

| Date | Change | Reason |
| --- | --- | --- |
| 2026-04-04 | Initial impldoc drafted for Slice 3b | Codegen and evaluation integration phase of v2 extraction runtime work. Completes runtime path after Slice 3a contract/validation complete. |
