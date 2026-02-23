\# QueryLens — EF Core SQL Preview Toolkit



\## Project overview

Building a .NET library + CLI tool + MCP server that translates 

EF Core LINQ expressions to SQL without running the app.

MySQL/Pomelo provider first. See architecture in /docs/DESIGN.md.



\## Tech stack

\- .NET 8, C# 12

\- Pomelo.EntityFrameworkCore.MySql (MySQL provider)

\- Microsoft.CodeAnalysis.CSharp.Scripting (Roslyn scripting)

\- ModelContextProtocol SDK for .NET (MCP server)

\- System.CommandLine (CLI)

\- xUnit + TestContainers (tests)



\## Build commands

dotnet build

dotnet test

dotnet run --project src/QueryLens.Cli -- translate --help



\## Architecture decisions

\- Each project assembly loads into its own isolated AssemblyLoadContext

\- ToQueryString() is the only public EF API we depend on — no internals

\- MCP server, CLI, and analyzer are thin hosts over QueryLens.Core

\- All transport-agnostic output goes through QueryTranslationResult record



\## Current phase

Phase 1: QueryLens.Core + QueryLens.MySql

Target: ToQueryString() working against sample app's DbContext



\## Test MySQL connection (Docker)

docker run -d --name querylens-mysql -p 3306:3306 \\

&nbsp; -e MYSQL\_ROOT\_PASSWORD=querylens \\

&nbsp; -e MYSQL\_DATABASE=querylens\_test \\

&nbsp; mysql:8.0



\## Do not

\- Take dependencies on EF Core internals (they break every minor version)

\- Reference QueryLens.Core from QueryLens.Analyzer (different process boundary)

\- Add framework-specific code to QueryLens.Core

```



---



\*\*How to actually work with Claude Code on this:\*\*



Open Claude Desktop → Code tab → point it at your QueryLens folder. Then work in focused phases, one per session. Don't try to build everything in one session — Claude Code works best with clear, bounded tasks:

```

Session 1:

"Set up the solution structure with the projects listed in CLAUDE.md.

&nbsp;Create QueryLens.Core with the IQueryLensEngine interface and all 

&nbsp;the records we defined. Add QueryLens.MySql stub. Wire up the 

&nbsp;solution file. No implementation yet, just the contracts."



Session 2:

"Implement the AssemblyLoadContext loading in QueryLens.Core.

&nbsp;It should load a given assembly path + all its dependencies 

&nbsp;from the same directory into an isolated collectible ALC.

&nbsp;Add unit tests. Use the SampleApp in /samples as the test subject."



Session 3:

"Implement the Roslyn scripting sandbox in QueryLens.Core.

&nbsp;It should take a DbContext instance and evaluate a LINQ expression

&nbsp;string against it, returning the IQueryable result for ToQueryString().

&nbsp;Cache warm ScriptState per assembly path."

