# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog.

## [Unreleased]

## [1.0.10] - 2026-03-29

### Fixed
- Hovering `await` on a query materialisation (e.g. `(await queryA.Concat(queryB).ToListAsync(ct)).ToList()`) no longer produces CS4032 ("The 'await' operator can only be used within an async method"). The extractor now strips any in-memory operations chained after an `await` result and forwards only the EF LINQ chain to the eval engine.
- VS extension: added Professional and Enterprise edition installation targets (was Community-only, blocking install on VS Professional/Enterprise)
- VS extension: widened `.NET 10.0 Runtime` prerequisite version range from `[18.4.11602.120,19.0)` to `[18.4.11602.120,)` so the extension installs on VS 2026 where the component version exceeds 19.0

## [1.0.9] - 2026-03-28

### Fixed
- VS Code action links (Copy SQL, Open SQL, Reanalyze) restored — now use `efquerylens://` scheme directly instead of the removed HTTP action server

### Changed
- Removed HTTP action server entirely; Rider uses Alt+Enter intention actions via LSP, VS Code uses `efquerylens://` URI scheme
- README: live version badges for all three marketplaces (Rider plugin now approved on JetBrains Marketplace)

## [1.0.8] - 2026-03-28

### Fixed
- VS Code Marketplace: removed `"preview": true` flag so the extension no longer shows the Preview badge

## [1.0.7] - 2026-03-28

### Added
- Rider: Alt+Enter intention actions (Copy SQL, Open SQL, Reanalyze) directly in hover popups via `EFQueryLensHoverIntentionAction`

### Changed
- Replaced `EFQueryLensDocumentationLinkHandler` with `EFQueryLensHoverIntentionAction` for hover popup action link handling
- Integration tests added for Rider Alt+Enter actions and action server HTTP routing

## [1.0.6] - 2026-03-26

### Fixed
- Rider plugin: split `dotnetPublish` arguments onto separate lines to satisfy ktlint `argument-list-wrapping` rule (ktlint 14.2.0+)
- Rider plugin: removed deprecated `kotlin.incremental.useClasspathSnapshot` Gradle property
- LSP project: bumped `Microsoft.CodeAnalysis.CSharp.Workspaces` to 5.3.0 to resolve NU1107 version conflict with Core

### Changed
- Rider plugin now publishes to the stable JetBrains Marketplace channel (was: preview)
- Dependency updates: Kotlin 2.2.0 → 2.3.20, ktlint plugin 14.1.0 → 14.2.0, `Microsoft.CodeAnalysis.CSharp` 5.0.0 → 5.3.0, various GitHub Actions versions

## [1.0.3] - 2026-03-26

### Fixed
- VS Code Marketplace publish: `--target` and `--packagePath` are no longer passed
  simultaneously to `vsce publish`
- Rider Gradle build: project paths now resolved outside `doLast` to avoid
  Gradle configuration cache `$$implicitReceiver_Project` NPE
- VS extension publish manifest now includes required `identity.internalName` field
- VS Code target names correctly mapped to .NET RIDs (`darwin-arm64` → `osx-arm64`,
  `win32-x64` → `win-x64`, etc.) in `prepareRuntime.mjs`
- Rider plugin no longer uses `@ApiStatus.Internal` JetBrains Platform APIs

## [1.0.2] - 2026-03-26

### Fixed
- Rider plugin no longer uses `@ApiStatus.Internal` JetBrains Platform APIs (`ContentUpdater`, `DocumentationLinkHandler.contentUpdater`). Hover popup action links (Copy SQL, Open SQL, Reanalyze) are now handled entirely by the public `UrlOpener` EP, which already intercepted `efquerylens://` scheme links before the OS shell.

## [1.0.1] - 2026-03-26

### Fixed
- VS extension hover returning null when cursor lands inside a lambda body (e.g. `w` in `.Where(w => w.IsNotDeleted())`)

### Added
- Reanalyze action link in hover popups for VS Code and Rider
- Cross-platform Rider plugin: daemon AppHost launchers bundled for win-x64/arm64, linux-x64/arm64, osx-x64/arm64
- `<RollForward>LatestMajor</RollForward>` on LSP and Daemon — users on .NET 8/9 no longer get a hard startup failure

### Changed
- Release pipeline publishes to stable channels across all three marketplaces
- VS extension now versioned and published to Visual Studio Marketplace via CI

## [1.0.0] - 2026-03-25

### Added
- Active plugin support across VS Code, Rider, and Visual Studio
- Marketplace-oriented plugin README pages for VS Code and Visual Studio
- Public docs page for factory placement and multi-DbContext setup (`docs/factory-setup.md`)

### Changed
- Root README rewritten for OSS branding, screenshots, and 3-IDE support
- VS Code plugin metadata updated for publisher/repository/license identity
- Rider plugin vendor metadata aligned with OSS publisher identity
- Visual Studio VSIX metadata updated (publisher, display name, description, tags)
- IDE support, getting started, architecture, provider, CLI, and MCP docs refreshed
- GitHub issue template discussions link updated to repository URL

### Removed
- Stub provider projects (`QueryLens.MySql`, `QueryLens.Postgres`, `QueryLens.SqlServer`)
- Stub provider tests (`QueryLens.MySql.Tests`)
- LSP inline SQL preview handlers and preview service (`CodeLensHandler`, `InlayHintHandler`, `CodeLensPreviewService`)
- VS Code cursor duplicate commands (`efquerylens.showSqlFromCursor`, `efquerylens.copySqlFromCursor`)
- Rider shadow cache implementation (`EFQueryLensShadowLspCache`)
- Visual Studio legacy hover documentation popup artifacts
