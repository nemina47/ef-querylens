# LSP MetadataLoadContext Warmup States

## Overview

Move LSP-side metadata inspection off `Assembly.LoadFrom()` and onto `MetadataLoadContext` so QueryLens can inspect `DbContext` and entity metadata without pinning user build outputs. At the same time, make cold-start behavior explicit in preview UX by showing immediate state-driven status such as `QueryLens starting` and `warming up` instead of silent waiting.

## Scope Boundaries

**Feature context**: Slice 1 of LSP metadata loading and runtime readiness UX

**This slice delivers**:

- LSP-side metadata inspection via `MetadataLoadContext` for `DbContext` and `DbSet<TEntity>` member-type discovery
- Internal readiness/result states for cold start and metadata inspection outcomes
- Preview status messaging for startup and warmup states so users get immediate feedback during daemon initialization
- Best-effort parity with existing metadata reads without introducing daemon-owned shadow-path coordination

**Out of scope / deferred**:

- Daemon-published shadow-bundle manifests for LSP consumption _(candidate for future slice)_
- LSP requests that ask the daemon to extend or rebuild shadow bundles on demand _(candidate for future slice)_
- Re-architecting the LSP to depend exclusively on daemon-provided artifact generations _(candidate for future slice)_
- Broader preview UX redesign beyond the new startup/warmup status messages _(candidate for future slice)_

**Depends on**: _None_

## Requirements

- [x] Replace LSP metadata inspection paths that currently call `Assembly.LoadFrom()` against user build outputs with disposable `MetadataLoadContext` usage.
- [x] Preserve current successful member-type inference behavior for supported project outputs, including `DbContext` discovery, `DbSet<TEntity>` lookup, and deterministic member type-name emission.
- [x] Ensure LSP-side metadata inspection does not keep project bin-folder assemblies locked after inspection completes.
- [x] Surface immediate user-visible preview status for daemon cold start and warmup rather than silently blocking.
- [x] Treat metadata dependency-resolution misses as best-effort diagnostics first: continue preview generation when possible, log resolver failures, and show user-facing failure only when the miss materially prevents preview generation.

## Design Decisions

### Decision 1: Use MetadataLoadContext only on the LSP side in this slice

**Choice:** Migrate LSP metadata reads to `MetadataLoadContext` now without adding daemon shadow-bundle coordination in the same impldoc.

**Rationale:** This removes the immediate DLL-lock regression with the smallest architectural change. The LSP only needs inspection-only reflection for `DbContext` and entity member discovery, which is a direct fit for `MetadataLoadContext`. Deferring daemon shadow-path coordination keeps the slice independently verifiable and avoids coupling cold-start correctness to a new cross-process contract.

**Alternatives considered:**

- Ask the daemon for shadow-cache paths first — rejected for this slice because it introduces a new readiness/protocol contract and bundle lifecycle concerns before fixing the immediate lock source.
- Keep `Assembly.LoadFrom()` and rely on collectible ALCs later — rejected because the existing LSP path loads directly from user output and holds locks in the current process.

### Decision 2: Show explicit startup and warmup status in preview

**Choice:** Return explicit preview states such as `QueryLens starting` and `warming up` immediately during cold start instead of silently waiting for the daemon.

**Rationale:** QueryLens cannot complete SQL preview until the daemon is ready anyway, so the UI should tell the user what is happening rather than appear stuck. This also gives the implementation a clear contract for readiness transitions and makes cold-start behavior testable.

**Alternatives considered:**

- Silent blocking until the daemon is ready — rejected because it obscures system state and makes startup delays look like failures.
- Hard failure during warmup — rejected because startup is a transient state, not an error.

### Decision 3: Preserve parity-first behavior on metadata binding misses

**Choice:** Treat `MetadataLoadContext` binding failures as best-effort diagnostics. Keep preview generation moving when the missing metadata does not block the outcome; only surface an end-user failure when the inspection miss materially prevents the preview.

**Rationale:** The goal of this slice is safer loading with parity to existing behavior, not a new degraded-mode UX. `MetadataLoadContext` uses a stricter closed-world resolver, so rare dependency misses must be handled defensively, but they should not become visible user noise unless they change the result.

**Alternatives considered:**

- Always show `limited metadata` on any resolver miss — rejected because it would make internal best-effort conditions too user-visible.
- Fail the preview on any metadata resolver miss — rejected because it would regress successful scenarios that do not actually need the missing dependency.

## Implementation Plan

1. Inventory all LSP metadata inspection paths that currently touch user output assemblies and confirm the minimal set that must move to `MetadataLoadContext`.
2. Introduce a small LSP-side metadata inspection helper that owns resolver construction, `MetadataLoadContext` lifetime, and deterministic best-effort logging for dependency-resolution misses.
3. Migrate `DbContext` discovery and `DbSet<TEntity>` member-type extraction to the new helper while preserving deterministic type-name emission behavior.
4. Add internal readiness/status result types covering at least `starting`, `warming`, `ready`, and `preview-blocked` outcomes needed by the preview pipeline.
5. Wire preview generation to surface immediate startup/warmup status when the daemon is not yet ready, while preserving existing success and failure paths once runtime evaluation proceeds.
6. Add focused unit and integration tests for lock-free metadata inspection, readiness-state transitions, and parity regressions in member-type inference.
7. Run targeted manual smoke validation in at least one IDE host to confirm first-hover messaging, no bin-folder lock retention, and successful preview after warmup completes.

## Dependencies

- `System.Reflection.MetadataLoadContext` package in the LSP project
- Existing daemon warmup/readiness signaling already available to the preview pipeline, if sufficient for the new status mapping
- No impldoc dependency required before implementation starts

## Testing Strategy

### Unit Tests

- Verify metadata inspection can resolve `DbContext` and `DbSet<TEntity>` member types through `MetadataLoadContext` without relying on runtime execution.
- Cover deterministic type-name serialization for generic, nested, nullable, and collection member types encountered during metadata inspection.
- Verify resolver misses are logged and only produce blocking status when the preview pipeline genuinely lacks required metadata.

### Integration Tests

- Exercise the LSP preview path from cold start through warmup to ready state and verify the returned preview status changes are explicit and ordered.
- Verify previously reported regressions stay fixed: entity names such as `Type` do not reintroduce ambiguity, and metadata-based member inference still supports the SQL Server sample shape.
- Validate that first preview does not leave user output DLLs locked after inspection completes.

### Manual Smoke Tests

Steps for `end-to-end-testing.md`:

1. Open a sample project and trigger a query preview before warmup completes — expected result: preview shows an immediate startup/warmup status message.
2. Wait for warmup and trigger the same preview again — expected result: SQL preview succeeds without requiring IDE restart.
3. Rebuild the sample project after previewing — expected result: build outputs are not blocked by the LSP process.

## Acceptance Criteria

- [x] LSP metadata inspection no longer uses `Assembly.LoadFrom()` directly on project output assemblies.
- [x] First preview during cold start returns explicit startup/warmup status instead of silent waiting.
- [x] Successful metadata inspection remains behaviorally equivalent for supported scenarios covered by existing and new tests.
- [x] No reproducible project-output DLL lock remains attributable to the LSP metadata inspection path.
- [x] New unit and integration tests are written and passing.
- [x] Documentation updated after implementation (`features.md`, `INDEX.md`, `roadmap.md`, `todos.md`, `end-to-end-testing.md`).

## Review Findings

_Populated by the implementer after code review, security review, and quality analysis. Only findings that resulted in code changes are recorded here. Deferred items go to `todos.md`. Valid sources: `code-reviewer`, `red-team`, `query-analyzer`, `trivy`, `sonarqube`._

| Source | Finding | Resolution |
| --- | --- | --- |
| code-reviewer | Metadata DbContext map file lacked a file-level docstring after the loader migration. | Added a concise file-level docstring describing metadata-only type extraction and why `MetadataLoadContext` is used. |

## Quality Report

_Populated by the implementer after all scans complete. Captures the final quality snapshot for the permanent record._

### Security Scan (Trivy)

_"Security scan skipped — Trivy not installed."_

### Code Quality (SonarQube)

**Quality Gate**: _Skipped_

| Metric | Value | Threshold | Status |
| --- | --- | --- | --- |
| SonarQube configuration | Missing `.env.copilot` | Present and complete | Skipped |

#### Issues Summary

| Type | Count | Top Finding |
| --- | --- | --- |
| Bugs (reliability) | — | Skipped — SonarQube not configured |
| Vulnerabilities | — | Skipped — SonarQube not configured |
| Code Smells (maintainability) | — | Skipped — SonarQube not configured |
| Security Hotspots | — | Skipped — SonarQube not configured |

## Change Log

| Date | Change | Reason |
| --- | --- | --- |
| 2026-04-07 | Initial draft created | Plan LSP-side MetadataLoadContext migration and explicit cold-start preview status behavior without daemon shadow-path coordination in the same slice |
| 2026-04-07 | Implementation complete | Replaced LSP bin-folder reflection loads with MetadataLoadContext, added explicit first-hover warmup status flow, passed focused and full solution tests, and deferred daemon shadow-path coordination to follow-up planning. |