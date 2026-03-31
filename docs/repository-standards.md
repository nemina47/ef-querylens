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

### Microsoft OSS Alignment (Src Projects)

For C# projects under `src/`, follow a Microsoft .NET OSS-aligned baseline:

- Use nullable correctness and explicit argument validation for public entry points.
- Keep async APIs explicit and consistently named (`*Async`).
- Prefer precise exception types with actionable error messages.
- Control complexity in long methods and high-branching paths by extracting focused helpers.
- Keep file/class boundaries discoverable; avoid excessive partial fragmentation.

Initial enforcement is warning-first. Analyzer and code-style diagnostics should surface in local builds and CI without blocking PRs. After baseline cleanup, enforcement can ratchet to block new violations only.

### Enforcement Modes

- Stage A: warnings only (current default).
- Stage B: block on new violations in selected pilot areas.
- Stage C: selective hard-fail for high-value reliability and maintainability rules.

### Pilot Checklist (Src)

Use this checklist during the warning-first pilot in the following hotspot files:

- `src/EFQueryLens.Core/AssemblyContext/ProjectAssemblyContext.DbContextDiscovery.cs`
- `src/EFQueryLens.Core/Scripting/Evaluation/QueryEvaluator.EvaluationFlow.cs`
- `src/EFQueryLens.Lsp/Parsing/AssemblyResolver.HostResolution.cs`

Checklist for pilot PRs:

- Keep behavior stable: refactor for readability/maintainability first, feature changes separately.
- Reduce branching depth and long-method complexity by extracting named helpers.
- Keep exception messages actionable and consistent.
- Add intent comments where algorithmic flow is non-obvious.
- Address analyzer warnings in touched lines when practical; do not suppress without rationale.

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

