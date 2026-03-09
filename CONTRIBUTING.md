# Contributing

Thanks for contributing to EF QueryLens.

## Prerequisites

- .NET SDK 10
- Node.js 20+
- npm 10+

## Build

```bash
dotnet build EFQueryLens.slnx
```

## Test

```bash
dotnet test EFQueryLens.slnx
```

## VS Code Client Build

```bash
cd src/Plugins/ef-querylens-vscode
npm install
npm run compile
```

## Pull Requests

- Keep changes scoped and cohesive
- Add or update tests when behavior changes
- Update docs/README for user-visible changes
- Keep command and config naming under `efquerylens.*`
