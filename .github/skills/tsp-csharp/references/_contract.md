# Reference File Contract

Every reference file in `tsp-csharp/references/` uses the same section structure so the SKILL.md hub can link into them predictably and new references are consistent.

## Required Sections

1. **Purpose** — When to reach for this reference and what it governs
2. **Default Guidance** — The preferred default path in short, imperative bullets
3. **Avoid** — Anti-patterns and risky shortcuts this reference prevents
4. **Review Checklist** — Verification points for code review or implementation
5. **Related Files** — Links to adjacent references within this skill
6. **Source Anchors** — Official Microsoft or community documentation that informed the guidance

## Authoring Notes

- Keep lists short and decision-oriented — not tutorial prose
- Include code examples only when the pattern is non-obvious
- Cross-link to related references when a topic spans domains
- Start sections at H2 (reference files are not standalone pages)
- Prefer tables for anti-pattern → fix mappings
