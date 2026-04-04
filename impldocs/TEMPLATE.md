# Feature Name

## Overview

Brief description of what this feature does and why it's needed.

## Scope Boundaries

**Feature context**: _Standalone feature_ or _Slice N of [Parent Feature Name]_

**This slice delivers**:

- Deliverable 1
- Deliverable 2

**Out of scope / deferred**:

- Deferred item 1 _(planned for impldoc {id})_
- Deferred item 2 _(candidate for future work)_

**Depends on**: _None_ or _impldoc {id} must be complete first_

## Requirements

- [ ] Requirement 1
- [ ] Requirement 2
- [ ] Requirement 3

## Design Decisions

Document key design choices and their rationale. Include alternatives considered and why they were rejected.

### Decision 1: Title

**Choice:** What was decided

**Rationale:** Why this option was chosen

**Alternatives considered:**

- Option A — why rejected
- Option B — why rejected

## Implementation Plan

Step-by-step implementation order. Each step should be small enough to verify independently. Target 5–7 steps. If you need more than 10, consider whether this impldoc should be split further.

1. Step one
2. Step two
3. Step three

## Dependencies

- External packages or services required
- Other impldocs this builds on

## Testing Strategy

### Unit Tests

- What to test
- Key edge cases

### Integration Tests

- Cross-module scenarios to verify

### Manual Smoke Tests

Steps for `end-to-end-testing.md`:

1. Step one — expected result
2. Step two — expected result

## Acceptance Criteria

- [ ] Criterion 1
- [ ] Criterion 2
- [ ] All existing tests pass
- [ ] New unit tests written and passing
- [ ] Documentation updated (features.md, INDEX.md, roadmap.md, todos.md, end-to-end-testing.md)

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
