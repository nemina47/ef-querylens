---
name: release
description: "Use when: creating a release, publishing a version, cutting a release, tagging a release, releasing plugin, release workflow, prepare release, create git tag, create GitHub release, push release branch, update version, bump version, update changelog, run release script, package plugin, publish to marketplace. Covers: version validation across manifests, git branch + tag creation, running scripts/release.ps1, changelog extraction, and communicating version numbers to user."
---

# Release Skill

Use this skill whenever the user asks to cut a release, tag a version, publish plugins, or create a GitHub release for QueryLens.

## Version Manifest Locations

Always verify and bump **all three** before building:

| Plugin | File | Field |
|---|---|---|
| VS Code | `src/Plugins/ef-querylens-vscode/package.json` | `"version"` |
| Rider | `src/Plugins/ef-querylens-rider/gradle.properties` | `pluginVersion` |
| Visual Studio | `src/Plugins/ef-querylens-visualstudio/EFQueryLens.VisualStudio/source.extension.vsixmanifest` | `<Identity ... Version="...">` |

Cross-check all three before proceeding. If any are inconsistent, resolve with the user before continuing.

## Release Workflow

### Step 1 — Confirm version

Ask for (or confirm from context) the target version string (e.g. `0.0.3`). Identify whether it is a preview or stable release. Use `v<version>-preview` for previews, `v<version>` for stable.

### Step 2 — CHANGELOG

Update `CHANGELOG.md`:
- Rename `## [Unreleased]` to `## [<version>] - <date>` (ISO 8601, e.g. `2026-03-19`)
- Add a fresh `## [Unreleased]` section above it

Commit this change together with the version bump in Step 4.

### Step 3 — Bump version manifests

Edit all three manifest files listed above to the new version. Keep the changes in a single commit.

### Step 4 — Branch and commit

```powershell
git checkout -b release/v<version>
git add CHANGELOG.md src/Plugins/ef-querylens-vscode/package.json src/Plugins/ef-querylens-rider/gradle.properties "src/Plugins/ef-querylens-visualstudio/EFQueryLens.VisualStudio/source.extension.vsixmanifest"
git commit -m "chore(release): bump version to <version>"
git push -u origin release/v<version>
```

### Step 5 — Run the release script

```powershell
./scripts/release.ps1 -Version <version>
```

Output lands in `releases/<version>/`:
- `vscode/ef-querylens-vscode-<version>.vsix`
- `rider/ef-querylens-rider-<version>.zip`
- `visualstudio/EFQueryLens.VisualStudio.vsix`
- `release-notes.md` (extracted from CHANGELOG)

Use `-SkipBuild` to reuse existing artifacts if already built. Use `-Clean` to wipe and rebuild from scratch.

#### Known Gradle quirk

`buildSearchableOptions` requires a live Rider headless session — the script already skips it with `-x buildSearchableOptions`. This is expected. The plugin is fully functional without it.

### Step 6 — Tag

```powershell
git tag v<version>-preview   # preview
# or
git tag v<version>           # stable
git push origin v<version>-preview
```

If moving an existing tag: `git tag -f`, then `git push --force origin <tagname>`.

### Step 7 — GitHub Release

Create a GitHub release pointing at the tag:
- Title: `v<version>` (or `v<version> Preview`)
- Body: contents of `releases/<version>/release-notes.md`
- Attach all three artifacts from `releases/<version>/`
- Mark as pre-release if using a `-preview` tag

## Notes

- `releases/` is gitignored — artifacts are never committed.
- The VS extension manifest (`source.extension.vsixmanifest`) also contains a `<ReleaseNotes>` URL — update it to point at the new tag URL when bumping the VS extension version.
- The release script's `Get-ChangelogSection` extracts markdown from the first `## [...]` heading it finds. If CHANGELOG parsing fails, the script falls back to a placeholder — check `release-notes.md` before uploading.
