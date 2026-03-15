# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog.

## [Unreleased]

### Added
- EF QueryLens rebrand groundwork (`EFQueryLens.*` projects and namespaces)
- OSS baseline docs and community files

### Changed
- VS Code command/config namespace to `efquerylens.*`
- Solution renamed to `EFQueryLens.slnx`
- LSP now exposes hover-based SQL preview without inline inlay/code-lens preview surfaces
- Rider plugin launches LSP directly from packaged runtime paths (shadow cache removed)
- VS Code package metadata now includes bundled `server/` and `daemon/` runtime payloads

### Removed
- Stub provider projects (`QueryLens.MySql`, `QueryLens.Postgres`, `QueryLens.SqlServer`)
- Stub provider tests (`QueryLens.MySql.Tests`)
- LSP inline SQL preview handlers and preview service (`CodeLensHandler`, `InlayHintHandler`, `CodeLensPreviewService`)
- VS Code cursor duplicate commands (`efquerylens.showSqlFromCursor`, `efquerylens.copySqlFromCursor`)
- Rider shadow cache implementation (`EFQueryLensShadowLspCache`)
- Visual Studio legacy hover documentation popup artifacts
