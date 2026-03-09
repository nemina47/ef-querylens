\# QueryLens — Architecture \& Design Document



\## Overview



QueryLens is a .NET toolkit that provides SQL visibility for EF Core LINQ queries at every stage of the developer lifecycle — while writing code, during MR review, and at runtime. It does this without requiring the application to run.



The core insight: `IQueryable.ToQueryString()` generates SQL without a real database connection. Everything in this project is infrastructure around making that call fast, ergonomic, and useful across multiple surfaces.



---



\## Guiding Principles



\*\*1. No EF Core internals\*\*

We depend only on `ToQueryString()` — the stable public API. Any dependency on internal EF Core types (query translators, expression visitors, etc.) will break on every minor version. This is a hard constraint.



\*\*2. Transport-agnostic core\*\*

The engine is a pure library. The CLI, MCP server, and IDE analyzer are thin hosts. No UI, no transport, no IDE SDK references in `EFQueryLens.Core`.



\*\*3. AssemblyLoadContext isolation is non-negotiable\*\*

User assemblies load into their own isolated, collectible ALC. This prevents EF Core version conflicts between the tool's dependencies and the user's project — a real problem since EF Core ships frequently.



\*\*4. MySQL first, then Postgres, then SQL Server\*\*

Provider-specific code lives in separate packages behind a common interface. The core never references a specific provider.



\*\*5. Latency is a feature\*\*

For the IDE experience, SQL must appear in under 300ms or developers stop using it. Warm state caching per project is mandatory, not optional.



---



\## Repository Structure



```

QueryLens/

├── CLAUDE.md                        ← Claude Code session instructions

├── docs/

│   └── DESIGN.md                    ← this file

├── src/

│   ├── EFQueryLens.Core/              ← engine, interfaces, records — no provider refs

│   ├── EFQueryLens.MySql/             ← Pomelo bootstrap + MySQL explain parser

│   ├── EFQueryLens.Postgres/          ← (stub — Phase 2)

│   ├── EFQueryLens.SqlServer/         ← (stub — Phase 2)

│   ├── EFQueryLens.Cli/               ← dotnet global tool

│   ├── EFQueryLens.Mcp/               ← MCP server

│   └── EFQueryLens.Analyzer/          ← Roslyn analyzer (ships as NuGet to user project)

├── tests/

│   ├── EFQueryLens.Core.Tests/

│   ├── EFQueryLens.MySql.Tests/

│   └── EFQueryLens.Integration.Tests/ ← TestContainers, real MySQL

└── samples/

&nbsp;   └── SampleApp/                   ← dogfood EF Core project for testing

```



---



\## Package Dependency Graph



```

EFQueryLens.Core          (no EF, no provider — pure abstractions)

&nbsp;       ↑

EFQueryLens.MySql         (depends on Core + Pomelo.EntityFrameworkCore.MySql)

&nbsp;       ↑

EFQueryLens.Cli           (depends on Core + MySql + System.CommandLine)

EFQueryLens.Mcp           (depends on Core + MySql + ModelContextProtocol SDK)



EFQueryLens.Analyzer      (Roslyn analyzer — communicates with engine over IPC,

&nbsp;                         does NOT reference Core directly to avoid

&nbsp;                         pulling EF Core into the IDE process)

```



The IPC boundary between the analyzer and the engine (named pipe / Unix socket) is critical. The analyzer runs inside the VS/Rider process. We cannot have Pomelo or EF Core loaded there.



---



\## Core Engine



\### Primary Interface



```csharp

public interface IQueryLensEngine : IAsyncDisposable

{

&nbsp;   Task<QueryTranslationResult> TranslateAsync(

&nbsp;       TranslationRequest request,

&nbsp;       CancellationToken ct = default);



&nbsp;   Task<ExplainResult> ExplainAsync(

&nbsp;       ExplainRequest request,

&nbsp;       CancellationToken ct = default);



&nbsp;   Task<ModelSnapshot> InspectModelAsync(

&nbsp;       ModelInspectionRequest request,

&nbsp;       CancellationToken ct = default);

}

```



\### Request Types



```csharp

public sealed record TranslationRequest

{

&nbsp;   // LINQ expression as C# source text

&nbsp;   // e.g. "db.Orders.Where(o => o.UserId == 5).Include(o => o.Items)"

&nbsp;   public required string Expression      { get; init; }

&nbsp;   public required string AssemblyPath    { get; init; }

&nbsp;   public string? DbContextTypeName       { get; init; }  // null = auto-discover

&nbsp;   public string  ContextVariableName     { get; init; } = "db";

}



public sealed record ExplainRequest : TranslationRequest

{

&nbsp;   public required string ConnectionString { get; init; }

&nbsp;   public bool UseAnalyze                  { get; init; } = true;

&nbsp;   // UseAnalyze=true requires MySQL 8.0.18+ / Aurora 3.x

&nbsp;   // Falls back to EXPLAIN FORMAT=JSON if server version is older

}



public sealed record ModelInspectionRequest

{

&nbsp;   public required string AssemblyPath  { get; init; }

&nbsp;   public string? DbContextTypeName     { get; init; }

}

```



\### Result Types



```csharp

public sealed record QueryTranslationResult

{

&nbsp;   public bool                          Success      { get; init; }

&nbsp;   public string?                       Sql          { get; init; }

&nbsp;   public IReadOnlyList<QueryParameter> Parameters   { get; init; } = \[];

&nbsp;   public IReadOnlyList<QueryWarning>   Warnings     { get; init; } = \[];

&nbsp;   public string?                       ErrorMessage { get; init; }

&nbsp;   public TranslationMetadata           Metadata     { get; init; } = default!;

}



public sealed record TranslationMetadata

{

&nbsp;   public string   DbContextType       { get; init; } = default!;

&nbsp;   public string   EfCoreVersion       { get; init; } = default!;

&nbsp;   public string   ProviderName        { get; init; } = default!;

&nbsp;   public TimeSpan TranslationTime     { get; init; }

&nbsp;   public bool     HasClientEvaluation { get; init; }  // silent perf killer — always flag

}



public sealed record QueryParameter

{

&nbsp;   public required string  Name          { get; init; }

&nbsp;   public required string  ClrType       { get; init; }

&nbsp;   public string?          InferredValue { get; init; }  // from expression literals if detectable

}



public sealed record QueryWarning

{

&nbsp;   public required WarningSeverity Severity   { get; init; }

&nbsp;   public required string          Code       { get; init; }

&nbsp;   public required string          Message    { get; init; }

&nbsp;   public string?                  Suggestion { get; init; }

}



public enum WarningSeverity { Info, Warning, Critical }

```



\### Explain Result



```csharp

public sealed record ExplainResult : QueryTranslationResult

{

&nbsp;   public ExplainNode? Plan              { get; init; }

&nbsp;   public bool         IsActualExecution { get; init; }  // false = estimates only (no ANALYZE)

&nbsp;   public string?      ServerVersion     { get; init; }

}



// Provider-agnostic normalized plan node

// MySQL, Postgres, and SQL Server all parse to this same structure

public sealed record ExplainNode

{

&nbsp;   public required string              OperationType  { get; init; }

&nbsp;   public string?                      TableName      { get; init; }

&nbsp;   public string?                      IndexUsed      { get; init; }  // null = full scan

&nbsp;   public double                       EstimatedCost  { get; init; }

&nbsp;   public long                         EstimatedRows  { get; init; }

&nbsp;   public long?                        ActualRows     { get; init; }  // null if no ANALYZE

&nbsp;   public int?                         LoopCount      { get; init; }

&nbsp;   public IReadOnlyList<ExplainNode>   Children       { get; init; } = \[];

&nbsp;   public IReadOnlyList<QueryWarning>  Warnings       { get; init; } = \[];



&nbsp;   // Derived — ratio of actual/estimated rows, useful for visualizer color coding

&nbsp;   public double? RowEstimateAccuracy =>

&nbsp;       ActualRows.HasValue \&\& EstimatedRows > 0

&nbsp;           ? (double)ActualRows.Value / EstimatedRows

&nbsp;           : null;

}

```



---



\## Assembly Loading



Each project assembly loads into its own isolated `AssemblyLoadContext`. This is collectible so memory can be reclaimed when a project is closed or rebuilt.



```

ProjectAssemblyContext

&nbsp;   ├── AssemblyLoadContext (isolated, collectible)

&nbsp;   ├── Loads: user assembly + all dependencies from same directory

&nbsp;   ├── Exposes: FindDbContext(typeName?) → Type

&nbsp;   └── Dispose() → unloads ALC, reclaims memory

```



\*\*Key design decision:\*\* the ALC loads dependencies from the assembly's output directory, not from the tool's own dependency set. This is what prevents "EF Core version X in tool vs version Y in user project" conflicts.



\*\*Long-running processes (MCP server):\*\* maintain a warm `ProjectAssemblyContext` pool keyed by assembly path + last-modified timestamp. Invalidate on rebuild.



---



\## Roslyn Scripting Sandbox



The scripting sandbox evaluates LINQ expressions against a live `DbContext` instance without running the full application.



```

QueryEvaluator

&nbsp;   ├── Warm ScriptState cache (keyed by assemblyPath + dbContextType)

&nbsp;   ├── On first call: build ScriptState with user assemblies as references

&nbsp;   │   and DbContext instance as global variable "db"

&nbsp;   ├── On subsequent calls: ContinueWith(expression) on cached state

&nbsp;   └── Extract IQueryable result → call .ToQueryString()

```



The globals type exposes the `db` variable:



```csharp

public sealed class QueryScriptGlobals

{

&nbsp;   public DbContext db { get; set; } = default!;

}

```



User expressions write naturally against this: `db.Orders.Where(o => o.UserId == 5)`.



\*\*Cache invalidation:\*\* when the assembly's last-modified timestamp changes (i.e., a rebuild happened), discard the cached state and rebuild on next call. This keeps the experience fresh after code changes.



---



\## Provider Abstraction



Two interfaces, one per provider package:



```csharp

// Configures DbContextOptions with a fake connection string

// so ToQueryString() works without a real DB

public interface IProviderBootstrap

{

&nbsp;   string ProviderName { get; }

&nbsp;   DbContextOptions ConfigureOffline(Type dbContextType);

}



// Parses raw EXPLAIN output into normalized ExplainNode tree

public interface IExplainParser

{

&nbsp;   string ProviderName { get; }

&nbsp;   ExplainNode Parse(string rawExplainOutput);

}

```



\### MySQL Implementation Notes



Pomelo requires a `ServerVersion` hint even for offline usage. The bootstrap detects the server version from loaded assemblies if available, otherwise defaults to `8.0.0-mysql`.



The explain parser handles two formats:



\- `EXPLAIN FORMAT=JSON` — available on all MySQL 8.x versions, returns JSON tree with estimated costs

\- `EXPLAIN ANALYZE` — available on MySQL 8.0.18+ and Aurora 3.x, returns text tree with actual row counts



Aurora version mapping:

\- Aurora 3.x → MySQL 8.0 compatible → `EXPLAIN ANALYZE` available

\- Aurora 2.x → MySQL 5.7 compatible → `EXPLAIN FORMAT=JSON` only, flag in result



\### MySQL Warning Rules



| Code | Trigger | Severity |

|------|---------|----------|

| `FULL\_TABLE\_SCAN` | `type: ALL` in explain | Critical |

| `BAD\_ROW\_ESTIMATE` | actual/estimated ratio < 0.1 or > 10 | Warning |

| `USING\_FILESORT` | `Using filesort` without index | Warning |

| `USING\_TEMPORARY` | `Using temporary` | Warning |

| `NO\_JOIN\_INDEX` | JOIN with no index on join column | Critical |

| `DEPENDENT\_SUBQUERY` | `select\_type: DEPENDENT SUBQUERY` | Warning |

| `CLIENT\_EVALUATION` | `HasClientEvaluation: true` on metadata | Critical |



---



\## CLI Tool



Installed as a .NET global tool: `dotnet tool install -g querylens`



\### Commands



```bash

\# Translate LINQ to SQL (no DB needed)

querylens translate \\

&nbsp; --assembly ./bin/Debug/net8.0/MyApp.dll \\

&nbsp; --expression "db.Orders.Where(o => o.UserId == 5).Include(o => o.Items)" \\

&nbsp; --context MyApp.Data.AppDbContext \\

&nbsp; --output text|json



\# Run EXPLAIN against real DB

querylens explain \\

&nbsp; --assembly ./bin/Debug/net8.0/MyApp.dll \\

&nbsp; --expression "db.Orders.Where(o => o.UserId == 5)" \\

&nbsp; --connection "Server=localhost;Database=myapp;User=root;Password=..." \\

&nbsp; --format tree|json|html



\# Diff SQL between two assembly builds (for CI)

querylens diff \\

&nbsp; --before ./artifacts/before/MyApp.dll \\

&nbsp; --after  ./artifacts/after/MyApp.dll \\

&nbsp; --format markdown|json \\

&nbsp; --fail-on new-full-scans|any-warnings|none

```



\### Diff Output (Markdown, for MR comments)



```markdown

\## QueryLens SQL Diff



3 queries changed · ⚠️ 1 new warning · ✅ 2 unchanged



\### OrderService.cs:42 — Modified

\*\*Before:\*\*

```sql

SELECT `o`.`Id` FROM `Orders` WHERE `o`.`UserId` = @p0

```

\*\*After:\*\*

```sql

SELECT `o`.`Id`, `i`.`Id` FROM `Orders`

LEFT JOIN `Items` AS `i` ON `o`.`Id` = `i`.`OrderId`

WHERE `o`.`UserId` = @p0

```

⚠️ `FULL\_TABLE\_SCAN` — No index on Items.OrderId

Suggestion: `CREATE INDEX idx\_items\_orderid ON Items(OrderId);`

```



The `--fail-on` flag enables CI gating — the command exits with code 1 if the condition is met, blocking the merge.



---



\## MCP Server



Implemented using the official ModelContextProtocol SDK for .NET.



\### Tools



| Tool | Description | Needs DB |

|------|-------------|----------|

| `ef\_translate` | LINQ → SQL + warnings | No |

| `ef\_model` | Full entity model snapshot (tables, columns, relationships, indexes) | No |

| `ef\_explain` | Explain plan + warnings | Yes |

| `ef\_diff` | SQL diff between two assembly versions | No |



\### Tool Schemas



```csharp

// ef\_translate

Input:  expression (string), assemblyPath (string), dbContextType (string?)

Output: QueryTranslationResult (SQL, parameters, warnings, metadata)



// ef\_model

Input:  assemblyPath (string), dbContextType (string?)

Output: ModelSnapshot (entities, relationships, indexes)



// ef\_explain

Input:  expression (string), assemblyPath (string),

&nbsp;       connectionString (string), dbContextType (string?)

Output: ExplainResult (plan tree, warnings, isActualExecution)



// ef\_diff

Input:  beforeAssemblyPath (string), afterAssemblyPath (string)

Output: DiffOutput (changed queries, new warnings, hasBreakingChanges)

```



\### Session State



The MCP server maintains a warm engine instance per project. First call to a project pays the cold start (~2-3s for assembly loading + DbContext instantiation). Subsequent calls in the same session are fast (<100ms from cache).



---



\## Roslyn Analyzer



Ships as a NuGet package: `<PackageReference Include="EFQueryLens.Analyzer" Version="..." />`



Communicates with the engine over a local named pipe / Unix socket. The analyzer process (running inside VS/Rider) never loads EF Core or Pomelo directly.



\### Diagnostic IDs



| ID | Title | Severity |

|----|-------|----------|

| `QL0001` | SQL Preview (informational) | Info |

| `QL0002` | Full Table Scan | Warning |

| `QL0003` | Client-Side Evaluation | Warning |

| `QL0004` | Missing JOIN Index | Warning |

| `QL0005` | Unbounded Result Set | Info |



\### IDE Surfaces (Phase 2+)



\- \*\*Inlay hints\*\* — ghost text on terminal calls showing column count + estimated rows + cost tier

\- \*\*Hover (QuickInfo)\*\* — rich tooltip with full SQL, parameters, warnings on hover over terminal call

\- \*\*CodeLens\*\* — per-method query count + warning rollup

\- \*\*Diagnostics\*\* — squiggles on queries with detected issues

\- \*\*Code actions\*\* — "Add suggested index" quick fix generating DDL



---



\## Build Phases



\### Phase 1 — Core Engine (Weeks 1-2)

\- `EFQueryLens.Core`: interfaces, records, AssemblyLoadContext, Roslyn scripting sandbox

\- `EFQueryLens.MySql`: Pomelo bootstrap, EXPLAIN FORMAT=JSON parser, EXPLAIN ANALYZE parser, warning rules

\- Integration tests against SampleApp using TestContainers



\### Phase 2 — CLI Tool (Week 3)

\- `translate`, `explain`, `diff` commands

\- JSON and text output formats

\- CI exit code support for gating



\### Phase 3 — MCP Server (Week 3-4)

\- Four tools: `ef\_translate`, `ef\_model`, `ef\_explain`, `ef\_diff`

\- Warm session state per project

\- End-to-end testing with Claude Desktop



\### Phase 4 — Roslyn Analyzer (Weeks 5-8)

\- NuGet package with diagnostic rules

\- IPC channel to engine process

\- VS extension: inlay hints + hover



\### Phase 5 — Explain Visualizer (Post v1)

\- Visual plan tree in VS peek window

\- Color-coded cost nodes

\- Live explain against dev connection



\### Phase 6 — Postgres + SQL Server (Post v1)

\- Provider-specific parsers behind existing `IExplainParser` interface

\- No changes to Core, CLI, or MCP



---



\## Sample App



`/samples/SampleApp` is the dogfood project used for integration tests throughout development. It should have realistic entities:



```csharp

// Entities to cover common query patterns

Order         (Id, UserId, Total, CreatedAt, Status)

OrderItem     (Id, OrderId, ProductId, Quantity, UnitPrice)

User          (Id, Name, Email, CreatedAt)

Product       (Id, Name, CategoryId, Price, Stock)

Category      (Id, Name, ParentCategoryId)



// Relationships that exercise joins

Order.User         (many-to-one)

Order.Items        (one-to-many)

OrderItem.Product  (many-to-one)

Product.Category   (many-to-one)

Category.Parent    (self-referential)

```



Every new feature should be validated against a query on this model before marking complete.



---



\## Testing Strategy



\- \*\*Unit tests\*\* — engine contracts, parser logic, warning rules; no real DB needed

\- \*\*Integration tests\*\* — TestContainers spins up MySQL 8.0 for explain tests

\- \*\*Snapshot tests\*\* — SQL output for a fixed set of queries against SampleApp; fail on unexpected changes

\- \*\*End-to-end\*\* — CLI commands + MCP tool calls against SampleApp assembly



---



\## Key Decisions Log



| Decision | Rationale |

|----------|-----------|

| `ToQueryString()` as only EF API | Internals break on every minor version |

| Collectible ALC per project | Memory reclamation for long-running MCP server |

| IPC between analyzer and engine | Prevents EF Core loading into IDE process |

| Pomelo over Oracle's MySQL driver | Better maintained, wider community adoption |

| MySQL first | Team's existing stack (Aurora); immediate dogfooding possible |

| CLI before IDE | Validates engine with real surface before investing in VS SDK |

