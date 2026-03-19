# Repository Standards

This document defines the default engineering baseline for EF QueryLens as an open source .NET repository.

## Design Principles

- Keep shared behavior in the backend, not duplicated across plugins.
- Prefer small, reviewable pull requests over broad refactors.
- Keep public contracts stable and forward-compatible.
- Make local development predictable with pinned tools and documented workflows.

## Repository Hygiene

- Use the SDK version pinned in `global.json`.
- Follow repo-wide whitespace, newline, and indentation rules from `.editorconfig`.
- Treat generated plugin payloads and build output as non-source artifacts.
- Keep repository metadata accurate in package manifests and build properties.

## Code Standards

- Enable nullable reference types and implicit usings for .NET projects unless a project has a strong reason not to.
- Favor explicit, descriptive names over abbreviated names.
- Keep IDE plugin implementations thin; push reusable logic into shared runtime projects.
- Add tests for behavior changes in shared runtime and parser/translation code.
- Avoid introducing cross-project coupling when a contract in `EFQueryLens.Core` is the correct boundary.
- Use repository formatters instead of hand-formatting:
  - C#: CSharpier
  - Kotlin: ktlint
  - TypeScript/JSON/Markdown/YAML: Prettier

## Documentation Standards

- Update `README.md` for changes that affect installation, supported scenarios, or user-facing features.
- Update `CONTRIBUTING.md` when local development or validation steps change.
- Add focused docs in `docs/` when a workflow needs more than a short README note.

## Local Automation

The recommended local hook flow uses .NET Husky with `.husky/pre-commit`.

Suggested setup:

```bash
dotnet tool install --local Husky
dotnet tool run husky install
```

If Husky is already present in your local tool manifest, use:

```bash
dotnet tool restore
dotnet tool run husky install
```

Formatting commands:

```bash
dotnet tool run csharpier .
dotnet tool run csharpier check .
npm run format --prefix src/Plugins/ef-querylens-vscode
npm run format:check --prefix src/Plugins/ef-querylens-vscode
cd src/Plugins/ef-querylens-rider && ./gradlew ktlintFormat
cd src/Plugins/ef-querylens-rider && ./gradlew ktlintCheck
```

## Pull Request Expectations

- Build and test the smallest sensible scope before asking for review.
- Call out any intentionally skipped validation.
- Keep unrelated cleanup out of feature/fix PRs.
- Include docs updates when the change affects users or contributors.

