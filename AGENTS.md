# AGENTS

This repository uses a shared backend with thin IDE plugin hosts. Keep changes aligned with that boundary.

## Architecture

- Core contracts and engine logic live in `src/EFQueryLens.Core`.
- Shared backend services live in `src/EFQueryLens.Lsp`, `src/EFQueryLens.Daemon`, and `src/EFQueryLens.DaemonClient`.
- IDE-specific behavior belongs in `src/Plugins/ef-querylens-vscode`, `src/Plugins/ef-querylens-rider`, and `src/Plugins/ef-querylens-visualstudio`.
- User-facing docs live under `docs/`.

## Working Rules

- Prefer backend changes over duplicating behavior in plugin clients.
- Keep plugin code thin: startup, packaging, commands, logging, and host-specific UI only.
- Preserve naming conventions:
  - .NET projects and namespaces use `EFQueryLens.*`
  - VS Code commands/settings use `efquerylens.*`
- Do not commit generated runtime payloads or local build artifacts.
- Update docs when behavior, setup, or contributor workflow changes.

## Validation

Run the smallest relevant validation first, then broaden if the change spans multiple areas.

```bash
dotnet build EFQueryLens.slnx
dotnet test EFQueryLens.slnx
npm run compile --prefix src/Plugins/ef-querylens-vscode
cd src/Plugins/ef-querylens-rider && ./gradlew compileKotlin
```

## Pre-Commit Hook Intent

The repo includes a pre-commit hook under `.husky/pre-commit`. It is intentionally lightweight:

- restore local .NET tools when available
- build the solution
- run core tests
- compile the VS Code plugin

Keep hook latency reasonable. Full release validation belongs in CI, not in the local pre-commit path.

## Formatting

- C# and `.csx`: `dotnet tool run csharpier .` or `dotnet tool run csharpier check .`
- VS Code plugin TS/JSON/Markdown/YAML: `npm run format --prefix src/Plugins/ef-querylens-vscode`
- Rider plugin Kotlin: `./gradlew ktlintFormat` from `src/Plugins/ef-querylens-rider`

