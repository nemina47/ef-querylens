# Backend Implementation Flow

> Read by the `tsp-implementer` agent to orchestrate backend implementations.

## Tool Detection

Detect the project's backend tooling before running any commands:

### .NET Projects

- **Detect**: `*.sln` or `*.csproj` files present
- **Build**: `dotnet build --no-incremental`
- **Lint & Format**: `dotnet format --verbosity normal` + `dotnet build -p:TreatWarningsAsErrors=true`
- **Test**: `dotnet test --verbosity normal`
- **Coverage**: `dotnet test --collect:"XPlat Code Coverage"`

### Node.js Projects

- **Detect**: `package.json` with server framework (`express`, `fastify`, `hono`, `nestjs`)
- **Package manager**: Detect in order: (1) lock files: `pnpm-lock.yaml` → `pnpm`, `yarn.lock` → `yarn`, `bun.lockb` → `bun`, `package-lock.json` → `npm`; (2) `packageManager` field in `package.json`; (3) default to `npm`
- **Lint**: `package.json` scripts for `lint` → package manager command
- **Test**: `package.json` scripts for `test` → package manager command
- **Build**: `package.json` scripts for `build` → package manager command

### Java Projects

- **Detect**: `pom.xml`, `build.gradle`, or `build.gradle.kts`
- **Build**: `mvn -B compile` or `./gradlew build -x test`
- **Lint & Format**: `mvn -B spotless:check` / `mvn -B checkstyle:check` or `./gradlew spotlessCheck` / `./gradlew checkstyleMain` when those plugins are configured
- **Test**: `mvn -B test` or `./gradlew test`
- **Coverage**: `mvn -B verify` (JaCoCo XML) or `./gradlew test jacocoTestReport`

### Python Projects

- **Detect**: `pyproject.toml`
- **Environment / runner**: Prefer `uv`; fall back to the project's documented Python workflow only if it is already established
- **Build**: `uv build` when the project packages a distributable artifact; otherwise skip a dedicated build step
- **Lint & Format**: `uv run ruff format --check .` + `uv run ruff check .`
- **Test**: `uv run pytest`
- **Coverage**: `uv run pytest --cov=. --cov-report=xml`

## Steps

Execute these steps after implementation is complete. Skip steps whose agents or tools are not available.

### 1. Lint & Format

**Agent**: `tsp-linter`

Run the detected lint and format commands based on the project type. Fix auto-fixable issues, report the rest.

### 2. Test

**Agent**: `tsp-unit-tester`

Run the detected test command. Pass the list of new/modified source files, the feature description, and the testing strategy from the impldoc.

### 3. Query Analysis (if applicable)

**Agent**: `tsp-query-analyzer`

**Skip** if the feature does not involve database queries or if the agent is not available.

For .NET projects: check for `tools/EfQueryHarness/` and use `ToQueryString()` for SQL extraction.

For Java projects: enable Hibernate SQL logging or an equivalent datasource proxy in dev/test, inspect generated SQL, and check for missing indexes, N+1 queries, over-fetching, unbounded result sets, and weak transaction boundaries.

For Python projects: if using SQLAlchemy, enable SQL echo or inspect compiled SQL in tests, then check for missing indexes, N+1 queries, over-fetching, unbounded result sets, and sync-vs-async session misuse.

For other ORMs: analyze query patterns in code and inspect generated SQL when possible.

### 4. Security Scan (Trivy)

Same as default flow — run Trivy if available, parse with summary script. Backend-specific concerns:

- Known vulnerabilities in NuGet/npm packages
- Secrets in code or configuration
- Misconfigurations in Dockerfiles, compose files

### 5. SonarQube Analysis (if available)

Same as default flow — run SonarQube scan if configured.

Note: Java and Python presets each ship a root `.sonarsteps` template. Mixed Java+Python projects must choose and maintain one scan flow manually in v1 because the scanner orchestration expects a single root file.

### 6. Code Review

**Agent**: `tsp-code-reviewer`

Standard review against installed skills and instructions. The reviewer discovers all `.github/skills/*/SKILL.md` and relevant `.github/instructions/*.instructions.md` files dynamically.

### 7. Security Review

**Agent**: `tsp-red-team`

Standard security review. Backend-specific concerns:

- Authentication and authorization on every endpoint
- Data ownership and tenancy checks
- Input validation and injection prevention
- Secrets management
- CORS, rate limiting, security headers

### 8. Documentation

**Agent**: `tsp-doc-updater`

Update project documentation as normal.

## Monorepo Hints

- Working directory: detect from impldoc scope or nearest project file (`.csproj`, `package.json`, `pom.xml`, `build.gradle`, `build.gradle.kts`, `pyproject.toml`)
- For .NET solutions with multiple projects, run commands from solution root
- For Node.js monorepos, use workspace-scoped commands
- For Java multi-module builds, run Maven or Gradle commands from the aggregator root unless the impldoc is explicitly scoped to one module
- For Python monorepos, run commands from the service or package root that owns the nearest `pyproject.toml`
