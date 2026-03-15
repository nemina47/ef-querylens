# EF QueryLens

Hover-first SQL translation and analysis toolkit for Entity Framework Core.

EF QueryLens helps you inspect generated SQL from LINQ during development through IDE integrations and language tooling.

## Current Status

- LSP backend: active
- VS Code client: active
- CLI host: scaffolded (in progress)
- MCP host: scaffolded (in progress)
- Providers: MySQL/Postgres/SQL Server projects removed for lean core; provider packages will be reintroduced as implemented

## Repository Layout

```text
src/
  EFQueryLens.Core/
  EFQueryLens.Lsp/
  EFQueryLens.Cli/
  EFQueryLens.Mcp/
  EFQueryLens.Analyzer/
  Plugins/
    ef-querylens-vscode/
    ef-querylens-visualstudio/
    ef-querylens-rider/
tests/
  EFQueryLens.Core.Tests/
  EFQueryLens.Integration.Tests/
```

## Build

```bash
dotnet build EFQueryLens.slnx
```

## VS Code Client

```bash
cd src/Plugins/ef-querylens-vscode
npm install
npm run compile
```

## Naming Conventions

- .NET namespaces/projects: `EFQueryLens.*`
- VS Code command/config prefix: `efquerylens.*`
- npm package: `ef-querylens-vscode`
- Repository name: `ef-querylens`

## Factory Placement Rule

When implementing `IQueryLensDbContextFactory<TContext>`, place the factory in an executable startup project (`API`, `WorkerService`, `Console`, etc.), not in a class library.

QueryLens resolves dependencies from the selected executable assembly output and only accepts QueryLens/design-time factories declared in that executable assembly.

## Multiple DbContexts

QueryLens supports multiple DbContexts in the same executable project.

Use one of these patterns:

- Add one `IQueryLensDbContextFactory<TContext>` implementation class per DbContext.
- Or use a single class that implements `IQueryLensDbContextFactory<TContext>` for multiple context types (explicit interface implementation is supported).

When more than one DbContext type exists, QueryLens picks the best match from the query expression; if it cannot disambiguate, specify the context type explicitly (for CLI via `--context`).

## Roadmap

- Complete CLI command surface
- Complete MCP tool surface
- Add Visual Studio and Rider plugin wrappers over LSP
- Reintroduce provider packages as production-ready modules

## License

MIT
