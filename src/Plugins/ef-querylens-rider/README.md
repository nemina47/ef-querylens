# EF QueryLens for Rider

Preview your EF Core SQL in real time, without leaving your IDE.

EF QueryLens for Rider integrates with the QueryLens LSP backend and gives you SQL visibility directly from your LINQ queries.

## Features

- Hover SQL preview for LINQ/EF queries
- Copy SQL action from hover
- Open SQL action in a dedicated preview dialog
- Refresh analysis action
- Structured split-query rendering

## Screenshot

![EF QueryLens Rider](https://raw.githubusercontent.com/querylenshq/ef-querylens/main/docs/assets/rider_plugin_single_query.png)

## Requirements

- JetBrains Rider (2025.2+)
- .NET 10 Runtime + ASP.NET Core Runtime (required to run the bundled QueryLens backend)
- .NET 10 SDK (only for local development/build)
- EF Core project

## Development

From `src/Plugins/ef-querylens-rider`:

1. `./gradlew build`
2. `./gradlew runIde`

Build backend runtime inputs first so Rider can bundle and launch them:

- `dotnet build src/EFQueryLens.Lsp/EFQueryLens.Lsp.csproj`
- `dotnet build src/EFQueryLens.Daemon/EFQueryLens.Daemon.csproj`

## Debugging (logs)

To investigate **copy sql / open sql / refresh** behavior or hover highlighting:

1. Open Rider logs (Help > Diagnostic Tools > Debug Log Settings, or open log in explorer).
2. Add logger category `efquerylens`.
3. Reproduce with a C# file containing EF Core queries.

Useful log entries:

- `[EFQueryLens] URL opener command=... uri=...` for action-link dispatch.
- `[EFQueryLens] URL opener failed for command=...` for command handling failures.
- `[EFQueryLens] applyHighlights: N entries` for hover highlight coverage.

## Troubleshooting file lock warnings

When running `runIde`, Rider sandbox lock warnings can appear if multiple IDE instances reuse the same sandbox.

Workaround:

1. Close all Rider windows and stop leftover Rider/Java processes.
2. Delete sandbox:

   ```powershell
   Remove-Item -Recurse -Force ".\\.intellijPlatform\\sandbox"
   ```

3. Start a single IDE instance:

   ```powershell
   .\gradlew runIde --no-daemon
   ```

4. Do not start another `runIde` session before closing the first one.
