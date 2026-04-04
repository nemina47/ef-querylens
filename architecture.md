# Architecture

<!--
  This file is human-authored and AI-read-only.
  AI agents read this for project context but must NEVER modify it.
  Only the developer (or team) should edit this document.

  Fill in each section to give AI agents reliable architectural context.
  Sections left empty are fine — agents will work with what's available.
  When you make significant structural changes, update this document
  and add an entry to the Change Log at the bottom.
-->

## System Purpose

_What does this system do? Who is it for? What problem does it solve?_

## Major Areas / Modules

_List the top-level areas, services, or modules. For each, give a one-line description of its responsibility._

<!--
  Example:
  - **api/** — REST API layer. Express routes, middleware, request validation.
  - **core/** — Business logic. Pure functions, no I/O dependencies.
  - **data/** — Data access. Repository pattern over PostgreSQL via Prisma.
  - **workers/** — Background jobs. Queue consumers for async processing.
-->

## Key Boundaries

_Where are the important architectural boundaries? What crosses them and what doesn't?_

<!--
  Example:
  - core/ never imports from api/ or data/ — dependency flows inward
  - All database access goes through data/repositories/ — no direct Prisma calls elsewhere
  - External API calls are isolated in integrations/ behind interface contracts
-->

## Critical Flows

_Describe the 2-5 most important runtime flows through the system._

<!--
  Example:
  1. **User login**: api/auth → core/auth.validate → data/users.findByEmail → core/tokens.issue → response
  2. **Order placement**: api/orders → core/orders.create → data/orders.insert → workers/notifications.enqueue
-->

## Invariants and Assumptions

_What must always be true? What assumptions does the architecture depend on?_

<!--
  Example:
  - Every API endpoint requires authentication except /health and /auth/login
  - All timestamps are stored as UTC
  - The system assumes a single PostgreSQL instance (no sharding)
  - Background jobs are idempotent — safe to retry on failure
-->

## Risky Areas

_What parts of the codebase are fragile, complex, or historically problematic?_

<!--
  Example:
  - Permission resolution in core/auth/permissions.ts — complex role inheritance, easy to break
  - Migration scripts in data/migrations/ — must be reviewed carefully, no rollback support yet
  - Rate limiting middleware — tuned for current load, needs revisiting if traffic doubles
-->

## Terminology

_Define project-specific terms that might be ambiguous._

<!--
  Example:
  - **Workspace**: a tenant-level container (not a VS Code workspace)
  - **Flow**: a user-defined automation pipeline, not a data flow
  - **Artifact**: a build output, not an impldoc review artifact
-->

## Notes for Planners

_What should the planner know when designing new features?_

<!--
  Example:
  - New API endpoints follow the route → handler → service → repository pattern
  - All new features need a feature flag — see core/flags/
  - Prefer extending existing modules over creating new top-level directories
-->

## Notes for Reviewers

_What should reviewers pay special attention to?_

<!--
  Example:
  - Any changes to data/migrations/ need manual verification against staging DB
  - Changes touching core/auth/ require security review
  - New dependencies must be justified in the impldoc
-->

---

## Change Log

| Date | Change | Author |
| --- | --- | --- |
| _(generated from template)_ | Empty — initial scaffold | _(developer)_ |
