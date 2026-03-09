# EF QueryLens Architecture

The detailed architecture remains in `docs/Design.md`.

This short page exists as the stable documentation entrypoint for external users.

## Core Components

- `EFQueryLens.Core`: engine contracts and execution core
- `EFQueryLens.Lsp`: language-server host used by IDE clients
- `EFQueryLens.Cli`: command-line host (in progress)
- `EFQueryLens.Mcp`: MCP host (in progress)
- `EFQueryLens.Analyzer`: analyzer host (planned)

## Design Principles

- Keep core provider-agnostic
- Keep transport/UI hosts thin
- Use isolated loading for user project assemblies
- Prefer explicit, stable naming conventions across clients
