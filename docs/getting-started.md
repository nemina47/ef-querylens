# Getting Started

## 1. Build

```bash
dotnet build EFQueryLens.slnx
```

## 2. Build VS Code extension client

```bash
cd src/Plugins/ef-querylens-vscode
npm install
npm run compile
```

## 3. Naming conventions

- .NET projects/namespaces: `EFQueryLens.*`
- VS Code command/config IDs: `efquerylens.*`

## 4. Current maturity

- LSP + VS Code path is the most complete.
- CLI and MCP hosts are currently scaffolded and will be expanded next.
