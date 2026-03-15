# EF QueryLens Rider Plugin

This project is a starter JetBrains Rider plugin that will run `EFQueryLens.Lsp` via Rider LSP support.

## Status

MVP LSP wrapper in place.

## Local dev

From `src/Plugins/ef-querylens-rider`:

1. `./gradlew build`
2. `./gradlew runIde`

Current plugin behavior:

- Registers an LSP support provider for `*.cs` files and starts `EFQueryLens.Lsp.dll` when a C# file is opened.
- **Hover SQL Preview:** Hover over a LINQ/EF query to view generated SQL and use the two actions: **Copy SQL** and **Open SQL Editor**.

## Debugging (logs)

To see why **Copy SQL / Open SQL Editor** links or the **hover highlight** might not work:

1. **Open the log:** **Help → Diagnostic Tools → Debug Log Settings** (or **Open log in Explorer**), or run Rider from a terminal and watch stdout.
2. **Enable EF QueryLens logs:** In Debug Log Settings, add a logger for `efquerylens` (or the plugin’s package). Then reproduce: open a C# file with a LINQ query, hover to open SQL preview, then click "Copy SQL" or "Open SQL Editor".
3. **What to look for:**
   - **`[EFQueryLens] resolveLink called: url=...`** — If this never appears when you click a link, the IDE is not passing doc popup link clicks to our handler (links may be disabled or handled elsewhere).
   - **`[EFQueryLens] Intercepted link: ...`** — Our handler accepted the link; next line should be command execution or **`[EFQueryLens] Failed to handle link:`** with an error.
   - **`[EFQueryLens] applyHighlights: N entries`** — Confirms highlights are applied; if N is 0 for that file, the blue hover highlight won’t show.

## Prerequisites


- Build the packaged runtime inputs first so the Rider plugin can bundle and launch them:
	- `dotnet build src/EFQueryLens.Lsp/EFQueryLens.Lsp.csproj`
   - `dotnet build src/EFQueryLens.Daemon/EFQueryLens.Daemon.csproj`
