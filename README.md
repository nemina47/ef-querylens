<div align="center">
  <img src="docs/assets/icons/ef-querylens_128x128.png" alt="EF QueryLens" width="96" />
  <h1>EF QueryLens</h1>
  <p><strong>See the SQL behind every LINQ expression — right in your editor.</strong></p>
  <p>
    <a href="https://marketplace.visualstudio.com/items?itemName=EFQueryLens.ef-querylens-vscode"><img src="https://img.shields.io/visual-studio-marketplace/v/EFQueryLens.ef-querylens-vscode?label=VS%20Code&color=0098FF" alt="VS Code Marketplace Version" /></a>
    <a href="https://marketplace.visualstudio.com/items?itemName=EFQueryLens.EFQueryLensVS"><img src="https://img.shields.io/visual-studio-marketplace/v/EFQueryLens.EFQueryLensVS?label=Visual%20Studio&color=5C2D91" alt="Visual Studio Marketplace Version" /></a>
    <a href="https://plugins.jetbrains.com/plugin/30753-ef-querylens"><img src="https://img.shields.io/badge/Rider-Under%20Review-orange" alt="Rider Marketplace Review" /></a>
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
    </tr>
    <tr>
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
    <td align="center" width="50%">
      <strong>Visual Studio</strong><br/><br/>
      <img src="docs/assets/vs_extension_single_query.png" alt="Visual Studio"  />
    </td>
    </tr>
    <tr>
    <td  align="center" width="50%">
      <strong>VS Code</strong><br/><br/>
      <img src="docs/assets/vs_code_plugin_single_query.png" alt="VS Code" />
    </td>
    </tr>
    <tr>
    <td  align="center" width="50%">
      <strong>JetBrains Rider</strong><br/><br/>
      <img src="docs/assets/rider_plugin_single_query.png" alt="Rider" />
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

> [!IMPORTANT]
> **Prerequisite:** Install **.NET 10** before using EF QueryLens.
> - **Extension usage:** .NET 10 Runtime + ASP.NET Core Runtime
> - **Building from source:** .NET 10 SDK
>
> EF QueryLens launches bundled backend processes via `dotnet`, so SQL preview features will not start if .NET 10 is missing.

Install quickly with the official installer scripts:

**PowerShell (Windows)**

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
.\dotnet-install.ps1 -Channel 10.0 -Runtime dotnet
.\dotnet-install.ps1 -Channel 10.0 -Runtime aspnetcore
# Optional (only for local build/dev):
.\dotnet-install.ps1 -Channel 10.0
```

**Bash (Linux/macOS)**

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0 --runtime dotnet
./dotnet-install.sh --channel 10.0 --runtime aspnetcore
# Optional (only for local build/dev):
./dotnet-install.sh --channel 10.0
```

**1. Add a factory to your executable project**

QueryLens needs a factory class to create an offline `DbContext` for SQL generation. Add it to your **executable startup project** (ASP.NET API, Worker Service, Console app) — not a class library.

First, define the interface
Then implement it. This is where you configure your provider, extensions like `UseProjectables()` or Gridify, query splitting behaviour, custom conventions — anything that affects the generated SQL:

```csharp
namespace EFQueryLens.Core;

public interface IQueryLensDbContextFactory<out TContext>
    where TContext : DbContext
{
    TContext CreateOfflineContext();
}


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
| VS Code | [Marketplace](https://marketplace.visualstudio.com/items?itemName=EFQueryLens.ef-querylens-vscode) |
| JetBrains Rider | [JetBrains Marketplace](https://plugins.jetbrains.com/plugin/30753-ef-querylens) *(under review)* |
| Visual Studio | [VS Marketplace](https://marketplace.visualstudio.com/items?itemName=EFQueryLens.EFQueryLensVS) |

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
