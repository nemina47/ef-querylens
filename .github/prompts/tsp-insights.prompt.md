---
name: tsp-insights
description: "Triage conversation insights. Reviews raw insights captured by agents, presents them with pros/cons analysis, and helps promote them to project instructions, user instructions, or org-wide suggestions — or dismiss them."
agent: "copilot"
argument-hint: "Optional: 'show all' to include low-confidence insights"
---

I want to triage my conversation insights.

{{ input }}

## Instructions

Read `.tsp-copilot/insights.md` and `.tsp-copilot/signals.jsonl` (if present).

### Step 1 — Compute Confidence

For each untriaged insight, extract its `insightId` from the `<!-- insightId:... -->` comment above it. Then find all signals in `signals.jsonl` with that `insightId`:

- **High**: any signal has `evidenceType: explicit_statement` or `correction`, OR 3+ signals with `evidenceType: explicit_acceptance` and no modifications
- **Medium**: 1–2 signals with `evidenceType: explicit_acceptance`, or signals with `outcome: modified_slight`
- **Low**: only `evidenceType: implicit_acceptance` signals, or signals with `outcome: modified_heavy`

If multiple untriaged entries share the same `insightId` (drift from different sessions), merge them into a single triage item using the most recent canonical wording.

Also scan `## Aliases` for existing mappings, and check whether any untriaged entries have insightIds that are semantically equivalent to other entries (in any section) but were minted with different slugs. If so, pick the **primary** insightId (prefer promoted > oldest), merge the entries, and record any new alias in `## Aliases`:

```markdown
- `{variant-insightId}` → `{primary-insightId}`
```

When computing confidence, aggregate signals across *both* the primary insightId and all its aliases.

### Step 2 — Filter

By default, show only **high** and **medium** confidence insights. If the developer said "show all", include low-confidence items too.

### Step 3 — Present Each Insight

For each insight, present:

1. **The insight** — category and one-line description
2. **Context** — what conversation/feature prompted it
3. **Confidence** — high/medium/low with reasoning
4. **Analysis** — brief pros and cons of adopting this as a standing preference. Include a short code sample if it clarifies the preference. Generate this analysis on the fly from the context — do not store it.
5. **Skill check** — search existing `.github/skills/`, `.github/instructions/`, and agent files. If the preference is already covered by an existing skill or instruction, note this: "Already covered by {file}. If you had to state it explicitly, the skill may not be surfacing reliably — flagging for tsp-instructions."

### Step 4 — Collect Decisions

For each insight, ask the developer to choose:
- **project** — promote to `copilot-instructions.md` (or a project instructions file)
- **user** — promote to `~/.github/copilot-instructions.md`
- **org** — suggest for org-wide adoption (append to `~/.tsp-copilot/org-suggestions.md`). Note: org suggestions are collected locally; upstream sync is not yet implemented.
- **dismiss** — not a standing preference, remove from untriaged

Accept decisions as a numbered list for efficiency (e.g., "1: project, 2: dismiss, 3: user").

### Step 5 — Promote with Consolidation

For each promotion:

1. Read the target file
2. Check for related existing entries
3. If a related entry exists, propose a **merged formulation** that combines both without redundancy. Show the current entry, the new insight, and the proposed merge. Wait for developer approval.
4. If no related entry exists, append the insight with a traceability comment:
   ```markdown
   <!-- insightId:{insightId} insightOrigin:{date}:{agent}:{feature-context} -->
   - {preference text}
   ```
5. Move the insight from `## Untriaged` to `## Promoted` — keep the `<!-- insightId:... -->` comment above the entry. Change the bullet format to include the target and date:

   ```markdown
   <!-- insightId:{insightId} -->
   - **[category]** canonical sentence — *promoted to {target} on {date}*
   ```

### Step 6 — Dismiss

Move dismissed insights from `## Untriaged` to `## Dismissed` — keep the `<!-- insightId:... -->` comment above the entry. Append the developer's reason:

```markdown
<!-- insightId:{insightId} -->
- ~~**[category]** canonical sentence~~ — *dismissed: {reason or "not a standing preference"}*
```

### Step 7 — Housekeeping

- If `## Untriaged` exceeds 20 items after triage, warn the developer
- Do **not** archive Promoted or purge Dismissed entries — they must remain visible for capture-time dedup. (Retention with a registry is deferred to Phase 2.)

### Skill Gap Detection

If any insights were already covered by existing skills/instructions but the developer had to state them explicitly, collect these into a summary at the end:

> **Skill gap signals**: These insights are already covered by existing customizations but were stated explicitly during conversations. This may indicate the skill descriptions need better trigger phrases or the instructions need to be more prominent.
>
> | Insight | Covered by | Suggested action |
> |---|---|---|
> | ... | ... | Update description triggers / Move to always-loaded instructions |

Suggest appending these to `~/.tsp-copilot/org-suggestions.md` for skill authoring review.
