---
description: "Rules for maintaining TSP project documentation files: features.md, impldocs, roadmap.md, todos.md, end-to-end-testing.md. Use when updating project docs after completing an impldoc."
applyTo: "**/impldocs/**, **/features.md, **/roadmap.md, **/todos.md, **/end-to-end-testing.md"
---

# Project Documentation Maintenance

## features.md

- Group features under `## Module Name` headings
- Each feature is a bullet: `- Description (impldoc-id)` where impldoc-id is the impldoc identifier (e.g., `user-auth-a1`)
- Keep descriptions concise — one line per feature
- Add features only after impldoc is completed, not when planned

## impldocs/INDEX.md

- One row per impldoc in the table: `| impldoc-id | status | summary |`
- Impldoc IDs use `{slug}-{suffix}`, where suffix is a 2-character lowercase alphanumeric disambiguator (e.g., `user-auth-a1`). Uniqueness applies to the full impldoc ID, not the suffix alone — the ID is a stable identifier, not an ordering indicator
- Status values: `planned`, `in-progress`, `completed`, `superseded`
- Superseded entries add `→ {id}` to reference the replacement (e.g., `→ user-auth-b2`)
- Summary is one sentence — this file is always loaded for context, keep it lean
- Never delete rows — impldocs are the historical record

## roadmap.md

- Remove items as they are completed
- Add new items discovered during implementation
- Keep items prioritized — most important first

## todos.md

- Remove items as they are addressed
- Add new deferred items discovered during implementation
- Include enough context that the item is actionable later
- Format: `- [ ] Description (discovered in impldoc-id)` linking to the impldoc where found

## end-to-end-testing.md

- Add smoke test steps under a heading matching the feature/module
- Steps should be reproducible by a new developer
- Format: numbered steps with expected results
- Keep existing steps unless the feature they test was replaced
