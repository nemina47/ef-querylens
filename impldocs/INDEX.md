# Implementation Documents

<!-- 
  Registry of all implementation documents (impldocs).
  Impldoc IDs use {slug}-{suffix}, where suffix is a 2-char lowercase alphanumeric
  disambiguator (e.g., user-auth-a1). Uniqueness applies to the full ID, not the suffix alone.
  The ID is a stable identifier, not an implementation order indicator.
  Status: planned | in-progress | completed | superseded
  
  When an impldoc is superseded, add "→ {id}" to reference its replacement.
  Keep summaries to one line — this file is always loaded for context.
-->

| Impldoc | Status | Summary |
| ------- | ------ | ------- |
| query-extraction-v2-q7 | completed | Slice 1 implements syntax-first extraction IR with boundary/root tracing, direct helper inlining for source-available IQueryable-returning helpers with multi-expression parameter support, and explicit diagnostics for unsupported shapes. |
| query-extraction-v2-capture-h2 | completed | Slice 2 replaces legacy symbol replay heuristics with deterministic capture-plan classification and explicit diagnostics. |
| query-extraction-v2-runtime-m6 | completed | Slice 3a completes transport layer: contracts, LSP→daemon pipeline, daemon validation, V2RuntimeAdapter foundation with 10 passing tests. Ready for 3b. |
| query-extraction-v2-runtime-3b | completed | Slice 3b integrates V2RuntimeAdapter into QueryEvaluator, updates codegen to consume capture-plan policies, adds 33 v2 tests (32 passing, 1 skipped), validates deterministic execution path selection, no regression in non-v2 paths. |
| v2-production-wiring-p9 | in-progress | Wires v2 capture-plan codegen into the production stub pipeline, adds StubSynthesizer.BuildV2Stubs adapter, passes v2Decision into TryBuildRunnerForCacheMiss, fixes Slice 3b review findings (unreachable code, comment mismatch, silent catch), adds pipeline+VS Code tests. Pending manual VS Code smoke validation. |
| v2-parity-extraction-k4 | in-progress | Accuracy-first extraction/runtime implementation in progress: core placeholder catalog and two-item deterministic collection seeding are now wired with focused tests. |
| rider-parity-stability-h7 | in-progress | Stabilizes Rider query preview parity with VS/VS Code via query-triggered extraction semantics plus Rider preview-only hover and full Alt+Enter action menu. |
| factory-root-substitution-j4 | completed | Adds safe root-receiver substitution for factory-created DbContext query chains so QueryLens runtime context executes supported factory patterns without placeholder-null failures. |
| query-extraction-v2-cutover-r4 | superseded → v2-cutover-inventory-k8 | Original broad Slice 4 cutover plan was split into smaller cleanup slices starting with inventory and guardrails. |
| v2-cutover-inventory-k8 | completed | Inventories remaining v1/legacy extraction and runtime paths, adds explicit unsupported-shape guardrails, and defines deletion slices for v2 cutover cleanup. |
| v2-cutover-core-runtime-s5 | in-progress | Removes legacy BuildInitialStubs dual-path dispatch from QueryEvaluator; migrates all QueryEvaluatorTests to v2 payloads; deletes StubSynthesizer.cs legacy file; removes LocalSymbolGraph from TranslationRequest. Blocking dependency for LSP cutover Slice 2. |
| lsp-metadata-loadcontext-r8 | completed | Replaces LSP bin-folder reflection loads with MetadataLoadContext and adds explicit cold-start preview status messaging without introducing daemon shadow-path coordination. |
