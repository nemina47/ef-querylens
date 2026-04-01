# Contributing

Thanks for contributing to EF QueryLens.

Start by reading `AGENTS.md` and `docs/repository-standards.md`. They define the repo boundary lines, contributor expectations, and the local validation baseline.

## Prerequisites

- .NET SDK 10
- Node.js 20+
- npm 10+
- JDK 21+ (for Rider plugin builds)

## Recommended Setup

Pin to the SDK in `global.json` and install local git hooks if you use Husky:

```bash
dotnet tool install --local Husky
dotnet husky install
```

If the tool is already installed in a local manifest:

```bash
dotnet tool restore
dotnet husky install
```

Helper scripts are available at `eng/install-hooks.ps1` and `eng/install-hooks.sh`.

## Formatting

```bash
dotnet tool restore
dotnet tool run csharpier .
npm run format --prefix src/Plugins/ef-querylens-vscode
cd src/Plugins/ef-querylens-rider && ./gradlew ktlintFormat
```

Check formatting without rewriting files:

```bash
dotnet tool run csharpier check .
npm run format:check --prefix src/Plugins/ef-querylens-vscode
cd src/Plugins/ef-querylens-rider && ./gradlew ktlintCheck
```

## Build (root)

```bash
dotnet build EFQueryLens.slnx
```

## Test

```bash
dotnet test EFQueryLens.slnx
```

## VS Code Plugin

```bash
npm ci --prefix src/Plugins/ef-querylens-vscode
npm run compile --prefix src/Plugins/ef-querylens-vscode
npm run test --prefix src/Plugins/ef-querylens-vscode
```

## Rider Plugin

```bash
cd src/Plugins/ef-querylens-rider
./gradlew build
./gradlew test
```

Run sandbox IDE for local plugin debugging:

```bash
./gradlew runIde --no-daemon
```

## Visual Studio Plugin

```bash
dotnet build src/Plugins/ef-querylens-visualstudio/EFQueryLens.VisualStudio/EFQueryLens.VisualStudio.csproj -c Debug
dotnet test tests/EFQueryLens.VisualStudio.Tests/EFQueryLens.VisualStudio.Tests.csproj -c Debug
dotnet test tests/EFQueryLens.VisualStudio.IntegrationTests/EFQueryLens.VisualStudio.IntegrationTests.csproj -c Debug
```

## Local Release Packaging

Build all three plugin artifacts into a single local release folder and generate release notes from `CHANGELOG.md`:

```powershell
pwsh ./scripts/release.ps1 -Version 0.0.2 -Clean
```

Output layout:

```text
releases/<version>/
	release-notes.md
	vscode/*.vsix
	rider/*.zip
	visualstudio/*.vsix
```

## Full Validation Sequence

```bash
dotnet build EFQueryLens.slnx
dotnet test EFQueryLens.slnx
npm run format:check --prefix src/Plugins/ef-querylens-vscode
npm run compile --prefix src/Plugins/ef-querylens-vscode
cd src/Plugins/ef-querylens-rider && ./gradlew ktlintCheck compileKotlin
```

## Pull Requests

- Keep changes scoped and cohesive
- Add or update tests when behavior changes
- Update docs/README for user-visible changes
- Keep command and config naming under `efquerylens.*`
- Keep plugin metadata (publisher/version/description) consistent across VS Code, Rider, and Visual Studio manifests
- Prefer changes in shared backend projects over reimplementing logic in individual plugins

## Src C# Standards (Warning-First)

For C# projects under `src/`, the repository uses a Microsoft .NET OSS-aligned standards baseline.

- Treat analyzer warnings as actionable feedback during development.
- Keep public async APIs suffixed with `Async`.
- Use explicit guard clauses for public entry points.
- Prefer targeted exception types over generic failures.
- Reduce complexity in long or heavily branched methods through helper extraction.

Current CI behavior is warning-first (non-blocking). Do not ignore recurring warnings in touched code; clean them up in-scope when practical.

