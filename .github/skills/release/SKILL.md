---
name: release
description: "Use when: creating a release, publishing a version, cutting a release, tagging a release, releasing plugin, release workflow, prepare release, create git tag, create GitHub release, update version, bump version, and update changelog. Covers: changelog extraction/review, semantic version validation, version bumping in VS Code + Rider manifests, guarded git commit/tag flow, and release status messaging."
---

# Release Skill

Use this skill whenever the user asks to cut a release, tag a version, publish plugins, or create a GitHub release for QueryLens.

## Release Workflow

### Step 1 — Verify git working tree is clean

Run:

```bash
git status --short
```

If any modified, staged, or untracked files are listed, stop immediately and tell the user:

> Working tree is not clean. Please commit or stash all changes before releasing.

### Step 2 — Read the current changelog

Read `CHANGELOG.md` from the repo root. Extract everything under the `## [Unreleased]` heading and stop at the next `## [` heading. Display that section to the user so they can review what is going into the release.

If the section is empty, ask:

> The [Unreleased] section is empty. Are you sure you want to release with no recorded changes? (yes/no)

If the user answers no, stop.

### Step 3 — Confirm the release version

If a version is supplied by the caller, use it. Otherwise ask for one (for example `0.0.3`).

Validate that it matches `MAJOR.MINOR.PATCH` (three numeric segments separated by dots). If invalid, ask again until valid. Store it as `VERSION`.

### Step 4 — Check the version does not already exist

Run:

```bash
git tag --list "v$VERSION"
```

If output is non-empty, stop and tell the user:

> Tag v$VERSION already exists. Choose a different version.

### Step 5 — Update CHANGELOG.md

Edit `CHANGELOG.md` at repo root:

1. Replace the `## [Unreleased]` line with:

   ```
   ## [Unreleased]

   ## [VERSION] - YYYY-MM-DD
   ```

   where `YYYY-MM-DD` is today's ISO date.

2. Keep the previously unreleased content under the new `[VERSION]` heading.
3. Leave the new `[Unreleased]` section intentionally empty as the next-cycle placeholder.

### Step 6 — Bump version references

Update exactly these files:

- `src/Plugins/ef-querylens-vscode/package.json` set `"version"` to `VERSION`
- `src/Plugins/ef-querylens-rider/gradle.properties` set `pluginVersion` to `VERSION`

### Step 7 — Commit the version bump

Stage exactly these files and commit:

```bash
git add CHANGELOG.md \
	src/Plugins/ef-querylens-vscode/package.json \
	src/Plugins/ef-querylens-rider/gradle.properties
git commit -m "chore: release v$VERSION"
```

Do not add any other files.

### Step 8 — Create and push the tag

Run:

```bash
git tag v$VERSION
git push origin HEAD
git push origin v$VERSION
```

The tag push triggers the GitHub Actions release workflow, which:
- Builds 6 platform-specific VS Code VSIXes and publishes them to the VS Code Marketplace
- Builds the Rider plugin ZIP (with daemon binaries for all 6 RIDs) and publishes it to JetBrains Marketplace
- Creates a GitHub Release with all artifacts attached

### Step 9 — Confirm success

Tell the user:

> ✅ Released v$VERSION
>
> - Commit pushed to origin
> - Tag v$VERSION pushed — CI is now building and publishing all three plugins
> - Check https://github.com/querylenshq/ef-querylens for pipeline status

## Notes

- This skill is intentionally aligned with `.claude/commands/release.md`.
- If release workflow changes, update both files in the same commit.
