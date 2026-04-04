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
| query-extraction-v2-runtime-m6 | in-progress | Slice 3a completes transport layer: contracts, LSP→daemon pipeline, daemon validation, V2RuntimeAdapter foundation with 10 passing tests. Ready for 3b. |
| query-extraction-v2-runtime-3b | planned | Slice 3b integrates V2RuntimeAdapter into QueryEvaluator, updates codegen to consume capture-plan policies, adds integration tests, validates SQL parity via harness. |
| query-extraction-v2-cutover-r4 | planned | Slice 4 performs final v2 cutover, removes legacy extraction/runtime paths, and validates end-to-end behavior. |
