# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog.

## [Unreleased]

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
