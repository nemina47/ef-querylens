<div align="center">
  <img src="docs/assets/icons/ef-querylens_128x128.png" alt="EF QueryLens" width="96" />
  <h1>EF QueryLens</h1>
  <p><strong>See the SQL behind every LINQ expression — right in your editor.</strong></p>
  <p>
    <img src="https://img.shields.io/badge/License-MIT-green.svg" alt="MIT License" />
  </p>
</div>

---

EF QueryLens translates EF Core LINQ to SQL at hover time. A shared daemon does the work; lightweight plugins for **VS Code**, **Rider**, and **Visual Studio** show the result. No database connection needed.

<br/>

## See it in action

<table>
  <tr>
    <td align="center" width="50%">
      <strong>Hover SQL preview</strong><br/><br/>
      <img src="docs/assets/onhover_sql.gif" alt="Hover SQL preview" />
    </td>
    <td align="center" width="50%">
      <strong>Edit &amp; re-preview</strong><br/><br/>
      <img src="docs/assets/onhover_edit_sql.gif" alt="Edit and re-preview" />
    </td>
  </tr>
</table>

<br/>

## Works everywhere

<table>
  <tr>
    <td align="center" valign="top" width="33%">
      <strong>Visual Studio</strong><br/><br/>
      <img src="docs/assets/vs_extension_single_query.png" alt="Visual Studio" width="100%" />
    </td>
    <td align="center" valign="top" width="33%">
      <strong>VS Code</strong><br/><br/>
      <img src="docs/assets/vs_code_plugin_single_query.png" alt="VS Code" width="100%" />
    </td>
    <td align="center" valign="top" width="33%">
      <strong>JetBrains Rider</strong><br/><br/>
      <img src="docs/assets/rider_plugin_single_query.png" alt="Rider" width="100%" />
    </td>
  </tr>
</table>

<br/>

## What you get

| Feature | Detail |
|---|---|
| Hover SQL preview | Generated SQL on any LINQ expression |
| Copy & Open actions | One-click copy or open in a dedicated SQL panel |
| Split-query rendering | Multi-statement queries shown with per-split labels |
| Shared backend | One daemon process, consistent results across all IDEs |
| Provider-aware | MySQL, PostgreSQL, SQL Server formatted correctly |
| Offline | No database connection — works from your compiled assembly |
| MCP server | Expose translations to AI agents and automation |

<br/>

## Quick Start

**1. Add a factory to your executable project**

QueryLens needs a factory class to create an offline `DbContext` for SQL generation. Add it to your **executable startup project** (ASP.NET API, Worker Service, Console app) — not a class library.

First, define the interface (or reference `EFQueryLens.Core`):

```csharp
namespace EFQueryLens.Core;

public interface IQueryLensDbContextFactory<out TContext>
    where TContext : DbContext
{
    TContext CreateOfflineContext();
}
```

Then implement it. This is where you configure your provider, extensions like `UseProjectables()` or Gridify, query splitting behaviour, custom conventions — anything that affects the generated SQL:

```csharp
public sealed class AppQueryLensFactory : IQueryLensDbContextFactory<AppDbContext>
{
    public AppDbContext CreateOfflineContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=offline;Database=offline")
            .UseProjectables()                              // Projectable properties
            .UseQuerySplittingBehavior(SplitQuery)          // Split-query rendering
            .Options;

        return new AppDbContext(options);
    }
}
```

The connection string is never opened — it only tells EF Core which provider dialect to use.

> See the full sample: [`samples/SampleMySqlApp/QueryLensDbContextFactory.cs`](samples/SampleMySqlApp/QueryLensDbContextFactory.cs)

**2. Build the solution**

```bash
dotnet build EFQueryLens.slnx
```

**3. Install the extension**

| IDE | Install |
|---|---|
| VS Code | [Marketplace](#) *(coming soon)* |
| JetBrains Rider | [JetBrains Marketplace](#) *(coming soon)* |
| Visual Studio | [VS Marketplace](#) *(coming soon)* |

**4. Hover any LINQ expression**

Point at a LINQ query. SQL appears. Use the hover actions to copy, open the full panel, or refresh.

<br/>

## Documentation

- [Getting Started](docs/getting-started.md)
- [IDE Support](docs/ide-support.md)
- [Architecture](docs/architecture.md)
- [Providers](docs/providers.md)
- [CLI Reference](docs/cli-reference.md)
- [MCP Server](docs/mcp-server.md)

<br/>

## Build & Test

<details>
<summary>Full build commands</summary>

```bash
# .NET solution
dotnet build EFQueryLens.slnx
dotnet test EFQueryLens.slnx

# VS Code plugin
npm ci --prefix src/Plugins/ef-querylens-vscode
npm run compile --prefix src/Plugins/ef-querylens-vscode

# Rider plugin
cd src/Plugins/ef-querylens-rider
./gradlew build
```

</details>

<br/>

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Security

See [SECURITY.md](SECURITY.md).

## License

MIT — see [LICENSE](LICENSE).
