---
description: "Setup guidance for SonarQube code quality integration. Loaded when working with SonarQube configuration, MCP server setup, or quality gate analysis."
applyTo: "**/{mcp.json,.env.copilot,.env.copilot.example,.sonarsteps}"
---

## SonarQube Setup

The SonarQube integration provides automated code quality analysis during the `tsp-implementer` workflow. It uses a local SonarQube Community instance via Docker and the official SonarSource MCP server for reading results. The scan process is language-agnostic — your `.sonarsteps` file defines the build/test/scan commands.

### Prerequisites

- **Docker** — [install Docker](https://docs.docker.com/get-docker/)
- **SonarQube scanner** appropriate to your language:
  - **.NET**: `dotnet tool install --global dotnet-sonarscanner`
  - **Other languages**: [sonar-scanner CLI](https://docs.sonarsource.com/sonarqube/latest/analyzing-source-code/scanners/sonarscanner/)
- **Coverage collector** appropriate to your test framework:
  - **.NET**: add `coverlet.collector` NuGet package to each test project (`dotnet add package coverlet.collector`)
  - **Node.js**: use `c8`, `istanbul`, or `nyc` to generate LCOV reports
  - Configure `sonar.*.reportsPaths` in your `.sonarsteps` to point to the coverage output

### Quick Setup

Run the setup script from your project root:

```bash
node .github/scripts/tsp-setup-sonarqube.js
```

This will:

1. Start a `sonarqube:community` Docker container (`tsp-sonarqube`)
2. Wait for SonarQube to become ready
3. Change the default admin password (stored in `~/.config/tsp-copilot/sq-credentials.json`)
4. Create a project matching your directory name
5. Configure the `tsp-strict` quality gate and assign it to the project
6. Generate a project-scoped API token
7. Write `SQ_URL`, `SQ_TOKEN`, and `SQ_PROJECT_KEY` to `.env.copilot`
8. Register the `tsp-sonarqube` MCP server in `.vscode/mcp.json`

### Configure Scan Steps

Create a `.sonarsteps` file in your project root with one command per line. Use `${SQ_URL}`, `${SQ_TOKEN}`, and `${SQ_PROJECT_KEY}` as placeholders — the scan script interpolates them at runtime.

**Example for .NET** (scaffolded automatically by the `csharp` preset):

```text
dotnet sonarscanner begin /k:"${SQ_PROJECT_KEY}" /d:sonar.host.url="${SQ_URL}" /d:sonar.token="${SQ_TOKEN}" /d:sonar.exclusions=".github/**,.vscode/**" /d:sonar.cs.opencover.reportsPaths="**/coverage.opencover.xml"
dotnet build
dotnet test --no-build --collect:"XPlat Code Coverage" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
dotnet sonarscanner end /d:sonar.token="${SQ_TOKEN}"
```

**Example for Node.js / generic**:

```text
sonar-scanner -Dsonar.projectKey="${SQ_PROJECT_KEY}" -Dsonar.host.url="${SQ_URL}" -Dsonar.token="${SQ_TOKEN}" -Dsonar.exclusions=".github/**,.vscode/**,node_modules/**" -Dsonar.javascript.lcov.reportPaths="coverage/lcov.info"
```

> **Note:** Add `.sonarsteps` to `.gitignore` — it is environment-specific (scanner paths may differ across machines).

### Manual Setup (alternative)

If you prefer to configure manually or connect to an existing SonarQube instance:

1. Set your SonarQube config in `.env.copilot`:

   ```text
   SQ_URL=http://localhost:9000
   SQ_TOKEN=your-token-here
   SQ_PROJECT_KEY=your-project-key
   ```

2. Verify the MCP server is configured in `.vscode/mcp.json`:

   ```json
   {
     "servers": {
       "tsp-sonarqube": {
         "type": "stdio",
         "command": "node",
         "args": [".github/scripts/tsp-start-sonarqube-mcp.js"]
       }
     }
   }
   ```

   This is installed automatically by `tsp-copilot init --preset mcp-sonarqube`.

3. Create a `.sonarsteps` file with your scan commands (see examples above).

### Running a Scan

The `tsp-implementer` runs scans automatically during implementation. To run manually:

```bash
node .github/scripts/tsp-run-sonar-scan.js
```

This reads `.sonarsteps`, interpolates `${SQ_URL}`, `${SQ_TOKEN}`, and `${SQ_PROJECT_KEY}` from `.env.copilot`, executes each command sequentially, then polls the SonarQube CE task until analysis is complete. Results are then available via the SonarQube MCP server tools or the dashboard.

### Troubleshooting

- **"Missing SonarQube config"** — run the setup script or add `SQ_URL`, `SQ_TOKEN`, and `SQ_PROJECT_KEY` to `.env.copilot`
- **".sonarsteps file not found"** — create a `.sonarsteps` file in your project root with your scan commands (see examples above)
- **Container won't start** — check port 9000 is free: `lsof -i :9000`
- **"Cannot authenticate"** — the setup script stores admin credentials in `~/.config/tsp-copilot/sq-credentials.json`. If you changed the password via the SQ web UI, update that file with your new password. This is only needed when setting up additional projects — existing projects use their own API token and are unaffected.
- **0% coverage in SonarQube** — ensure your `.sonarsteps` includes a test command with coverage collection, and that the `sonar.*.reportsPaths` property points to the coverage output. For .NET: add `coverlet.collector` NuGet package to test projects.
- **Scanner not found** — install the scanner for your language (see Prerequisites above)
- **Analysis timeout** — SonarQube may need more time for large projects. Check progress at `http://localhost:9000/project/background_tasks?id=YOUR_PROJECT_KEY`
- **macOS Docker networking** — the MCP launcher automatically handles macOS Docker Desktop networking (uses `host.docker.internal` instead of `localhost`)
- **Multiple projects** — the setup script reuses the same `tsp-sonarqube` Docker container and `tsp-strict` quality gate across projects. Each project gets its own API token (`tsp-copilot-{projectKey}`). Run the setup script from each project root.

### Security

- `.env.copilot` should be in `.gitignore` — never commit tokens or credentials
- The SonarQube MCP server runs in read-only mode (`SONARQUBE_READ_ONLY=true`)
- Admin credentials are stored in `~/.config/tsp-copilot/sq-credentials.json` (owner-only permissions `0600`)
- Add `.sonarqube/` and `.scannerwork/` to `.gitignore` — they contain scanner working files
