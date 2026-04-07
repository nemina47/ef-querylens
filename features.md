# Features

<!-- 
  This file tracks all implemented features, grouped by module/area.
  It is maintained by the developer (with AI assistance) after each impldoc is completed.
  Each feature references its impldoc ID for traceability.
  
  Format:
  ## Module Name
  - Feature description (impldoc-id)
-->

## Query Extraction

- V2 extraction IR (V2QueryExtractionPlan) representing boundary classification, root context, and extraction diagnostics (query-extraction-v2-q7)
- Boundary classification for materialized vs queryable query shapes (query-extraction-v2-q7)
- Syntax-first root tracing from hover position to DbSet query root (query-extraction-v2-q7)
- Direct IQueryable helper inlining with eligibility validation for source-available helpers (query-extraction-v2-q7)
- Multi-expression helper parameter support when return expression directly composes parameters (query-extraction-v2-q7)
- Explicit extraction diagnostics with error codes for unsupported helper shapes (query-extraction-v2-q7)
- LspSyntaxHelper.V2Extraction module with boundary/root/helper analysis pipeline (query-extraction-v2-q7)
- EF Query Harness skeleton tool for future SQL extraction and validation (query-extraction-v2-q7)
- V2 capture-plan contract types with deterministic capture classification (query-extraction-v2-capture-h2)
- LSP v2 capture planner with ReplayInitializer/UsePlaceholder/Reject logic and explicit diagnostics (query-extraction-v2-capture-h2)
- Adapter from v2 capture plan to legacy LocalSymbolGraph for current runtime compatibility (query-extraction-v2-capture-h2)
- HoverPreviewService pipeline integration with capture planner and diagnostic blocking (query-extraction-v2-capture-h2)
- V2 capture snapshot in daemon cache-key materialization (query-extraction-v2-capture-h2)

## Query Evaluation (V2 Runtime)

- V2RuntimeAdapter contract types for deterministic execution path selection (query-extraction-v2-runtime-m6)
- V2RuntimeAdapter.Analyze() decision logic validating extraction IR, capture plan completeness, and symbol resolution (query-extraction-v2-runtime-m6)
- Daemon-side V2RuntimeValidator for compile-time diagnostics and symbol availability checks (query-extraction-v2-runtime-m6)
- V2 request/response contract serialization and transport over LSP infrastructure (query-extraction-v2-runtime-m6)
- QueryEvaluator integration with V2RuntimeAdapter for deterministic v2 path selection with structured rejection diagnostics (query-extraction-v2-runtime-3b)
- EvalSourceBuilder.V2Support policy-driven code generation (ReplayInitializer/UsePlaceholder/Reject) for capture-plan entries (query-extraction-v2-runtime-3b)
- RunnerGenerator.V2Support initialization code generation for v2 capture-plan statements (query-extraction-v2-runtime-3b)
- Comprehensive v2 codegen unit tests validating policy interpretation and code emission (query-extraction-v2-runtime-3b)
- End-to-end v2 integration tests for direct chains, helper composition, and rejection scenarios (query-extraction-v2-runtime-3b)

## DbContext Support

- Factory-root receiver substitution for `IDbContextFactory<TContext>` -rooted LINQ chains with strict type-compatibility gates (factory-root-substitution-j4)
- Root-only rewriting strategy to avoid semantic drift in complex helper/subquery scenarios (factory-root-substitution-j4)
- Deterministic substitution/skip diagnostics for factory pattern matching in capture planning (factory-root-substitution-j4)
- Free variable collection filtering to exclude synthetic factory context receiver from capture graph (factory-root-substitution-j4)
- Unit test coverage for async/sync factory patterns, ambiguity conflicts, and regression cases (factory-root-substitution-j4)

## LSP Metadata

- MetadataLoadContext-based DbContext and entity metadata inspection without locking bin-folder DLLs (lsp-metadata-loadcontext-r8)
- Explicit first-hover startup and warmup status messaging during daemon cold start (lsp-metadata-loadcontext-r8)
- Shared WarmupHandler instance across LSP hover and warmup commands (lsp-metadata-loadcontext-r8)
- Deterministic fully qualified type-name expectations in metadata inspection tests (lsp-metadata-loadcontext-r8)
