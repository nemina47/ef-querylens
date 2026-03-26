# EF QueryLens for VS Code

Preview your EF Core SQL in real time, without leaving your IDE.

EF QueryLens for VS Code connects to the QueryLens language server and shows generated SQL directly from your LINQ queries.

## Features

- Hover SQL preview for EF Core LINQ
- Copy SQL from hover actions
- Open SQL in a dedicated preview window
- Refresh query analysis without leaving the editor
- Provider-aware SQL formatting controls

## Screenshot

![EF QueryLens VS Code](https://raw.githubusercontent.com/querylenshq/ef-querylens/main/docs/assets/vs_code_plugin_single_query.png)

## Requirements

- VS Code 1.80+
- .NET 10 Runtime + ASP.NET Core Runtime (required to run the bundled QueryLens backend)
- .NET 10 SDK (only for local development/build)
- An EF Core project

## Commands

- `EF QueryLens: Show SQL Preview`
- `EF QueryLens: Copy SQL`
- `EF QueryLens: Open SQL`
- `EF QueryLens: Refresh`
- `EF QueryLens: Restart Language Server`
- `EF QueryLens: Open Output`

## Settings

| Setting | Type | Default | Description |
|---|---|---|---|
| `efquerylens.codeLens.maxPerDocument` | number | `50` | Max query CodeLens entries per document. |
| `efquerylens.codeLens.debounceMs` | number | `250` | Cache window for unchanged document text. |
| `efquerylens.codeLens.useModelFilter` | boolean | `false` | Validate roots against model DbSet names. |
| `efquerylens.sql.formatOnShow` | boolean | `true` | Format SQL before showing or copying. |
| `efquerylens.sql.dialect` | string | `auto` | SQL formatter dialect. |
| `efquerylens.debug.enableVerboseLogs` | boolean | `false` | Enable verbose client/server logs. |

## Build From Source

```bash
npm ci
npm run compile
```

## More

- Repository: https://github.com/querylenshq/ef-querylens
- Docs: https://github.com/querylenshq/ef-querylens/tree/main/docs
