---
description: "C# coding conventions for TSP projects. Use when writing, reviewing, or modifying C# files."
applyTo: "**/*.cs"
---

# C# Conventions

Target .NET 8+ / C# 12+ unless constrained by the project TFM. Follow Microsoft .NET conventions as the baseline with TSP-specific overrides below.

## General

- Use **file-scoped namespaces** (`namespace MyProject.Models;`)
- Use **primary constructors** when they reduce boilerplate without sacrificing clarity
- Prefer `record` types (or `record struct`) for DTOs and value objects; use `class` only when mutable state or inheritance is required
- For public API models, prefer `required` properties over constructor parameters
- **Never** use `Console.WriteLine` in production code — inject and use `ILogger<T>`
- Follow **least-exposure**: default to `private` > `internal` > `protected` > `public`
- Do **not** add interfaces or abstractions unless needed for DI, testing, or external boundaries
- Do **not** wrap existing abstractions or create unnecessary layers
- Enable nullable reference types (`<Nullable>enable</Nullable>`) in every project
- Always use `var` for local variable declarations
- Use `decimal` for currency and financial values — never `float` or `double`
- Use collection expressions (`[]`), raw string literals (`"""`), and expression-bodied members where they improve readability
- When encountering unused methods, parameters, or variables — flag them for cleanup in `todos.md` rather than fixing immediately (avoid scope creep)

## Code Layout & Organization

- One public type per file (filename matches type name)
- Order members: fields → properties → constructors → methods → nested types
- Keep methods short (< 30 lines ideal); extract private helpers when logic exceeds one screen
- Use `using` declarations for `IDisposable` resources
- Braces on new line for types, methods, and control blocks

## Naming

- **PascalCase**: types, namespaces, public members, methods, properties, events, constants, enums
- **camelCase**: local variables, method parameters
- **_camelCase**: private fields (underscore prefix)
- **s_camelCase**: static private fields
- `I` prefix for interfaces only; no prefix for abstract classes
- Async methods **must** end with `Async` suffix
- Avoid abbreviations unless universally known (`Id`, `Db`, `Http`)
- Enums: singular for non-flags, plural for `[Flags]`

## Comments & Documentation

- Comments explain **why**, never **what**
- XML documentation (`///`) required for all public types and members
- Remove commented-out code before commit — use git history instead

## Error Handling

- Prefer **Result<T>** / **Option<T>** (or `OneOf`, `ErrorOr`) patterns for expected failures
- Throw exceptions only for truly exceptional conditions or programmer errors
- Use precise exception types (`ArgumentNullException`, `InvalidOperationException`) with `paramName`
- Use `ArgumentNullException.ThrowIfNull()` and `string.IsNullOrWhiteSpace` guards early
- **Never** catch `Exception` without logging and re-throwing
- No silent catches — always log at the appropriate level

## Immutability & Records

- Default to immutable types
- Use `init` setters or `required` properties
- Use `with` expressions for non-destructive mutation
- For value objects: `record struct` with `readonly`

## Dependency Injection

- Constructor injection preferred
- **Scoped** is the default lifetime for most services; use Transient/Singleton only with justification
- Avoid static service locators

## EF Core Query Harness

When an impldoc includes "Create EF Core query harness", build it as follows:

1. `dotnet new console -n EfQueryHarness -o tools/EfQueryHarness`
2. Edit `tools/EfQueryHarness/EfQueryHarness.csproj`:
   - Match the `TargetFramework` to the project's TFM (read from the main `.csproj` — do NOT hardcode)
   - Add a `ProjectReference` to the project containing the actual `DbContext` (e.g., `../../src/MyApp.Infrastructure/MyApp.Infrastructure.csproj`)
3. Write `tools/EfQueryHarness/Program.cs` as a top-level program:
   - Bootstrap the `DbContext` using `new DbContextOptionsBuilder<T>().UseSqlite(connectionString)` (or the provider the project uses)
   - Read the connection string the same way the main app does (from `appsettings.json` or hardcode the dev value)
   - Add marker comments for the query analyzer agent. The sample LINQ between markers is a **placeholder only** — the agent replaces it entirely each time:

```csharp
using var db = new AppDbContext(options);

// === PASTE LINQ EXPRESSION BELOW === (sample only — agent replaces this)
var query = db.Users.AsNoTracking().Where(u => u.IsDisabled == false);
// === END LINQ EXPRESSION ===

Console.WriteLine(query.ToQueryString());
```

4. Verify: `cd tools/EfQueryHarness && dotnet run` should print the SQL to stdout
5. Do **not** add to the `.sln` — this is a dev-time tool only
6. Total files: `EfQueryHarness.csproj` + `Program.cs` (2 files)

## Meta Rules

- Apply **SOLID** principles
- Prefer composition over inheritance
- Keep diffs small and focused
- Code must compile cleanly with **zero warnings** (treat warnings as errors in CI)
