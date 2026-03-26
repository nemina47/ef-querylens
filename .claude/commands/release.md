# Release preparation

Prepare and publish a new release for the EF QueryLens repository.
The version to release is: $ARGUMENTS

Follow every step below in order. Do not skip or reorder steps.

---

## Step 1 — Verify git working tree is clean

Run `git status --short`. If any modified, staged, or untracked files are listed,
stop immediately and tell the user:

> Working tree is not clean. Please commit or stash all changes before releasing.

---

## Step 2 — Read the current changelog

Read `CHANGELOG.md` from the repo root. Extract everything under the
`## [Unreleased]` heading (stop at the next `## [` heading). Display it to the
user so they can review what is going into this release.

If the `[Unreleased]` section is empty (no content under it), warn the user:

> The [Unreleased] section is empty. Are you sure you want to release with no
> recorded changes? (yes/no)

If they answer no, stop.

---

## Step 3 — Confirm the release version

If a version was supplied as `$ARGUMENTS`, use it. Otherwise ask:

> What version should this release be tagged as? (e.g. 0.0.3)

Validate the answer matches `MAJOR.MINOR.PATCH` (three numeric segments separated
by dots). If it does not, ask again. Remember this as `VERSION`.

---

## Step 4 — Check the version does not already exist

Run `git tag --list "v$VERSION"`. If output is non-empty, stop and tell the user:

> Tag v$VERSION already exists. Choose a different version.

---

## Step 5 — Update CHANGELOG.md

Edit `CHANGELOG.md` at the repo root:

1. Replace the `## [Unreleased]` line with two blocks:
   ```
   ## [Unreleased]

   ## [VERSION] - YYYY-MM-DD
   ```
   where `YYYY-MM-DD` is today's date in ISO 8601 format.

2. The content that was previously under `[Unreleased]` now sits under the new
   `[VERSION]` heading. The new `[Unreleased]` section above it is intentionally
   empty — it is the placeholder for the next release cycle.

---

## Step 6 — Bump version references

Update the following files so local tooling and IDE run-configurations stay in
sync (CI overrides these at publish time via the git tag, but keeping them current
avoids confusion):

- `src/Plugins/ef-querylens-vscode/package.json` — set `"version"` to `VERSION`
- `src/Plugins/ef-querylens-rider/gradle.properties` — set `pluginVersion` to `VERSION`

---

## Step 7 — Commit the version bump

Stage exactly these files and create a commit:

```
git add CHANGELOG.md \
        src/Plugins/ef-querylens-vscode/package.json \
        src/Plugins/ef-querylens-rider/gradle.properties
git commit -m "chore: release v$VERSION"
```

Do not add any other files.

---

## Step 8 — Create and push the tag

```
git tag v$VERSION
git push origin HEAD
git push origin v$VERSION
```

The tag push triggers the GitHub Actions release workflow, which:
- Builds 6 platform-specific VS Code VSIXes and publishes them to the VS Code Marketplace
- Builds the Rider plugin ZIP (with daemon binaries for all 6 RIDs) and publishes it to JetBrains Marketplace
- Creates a GitHub Release with all artifacts attached

---

## Step 9 — Confirm success

Tell the user:

> ✅ Released v$VERSION
>
> - Commit pushed to origin
> - Tag v$VERSION pushed — CI is now building and publishing all three plugins
> - Check https://github.com/nemina47/ef-querylens/actions for pipeline status
