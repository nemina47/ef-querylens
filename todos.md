# Todos

## Known Issues

- [ ] Evaluate a daemon-published shadow-bundle manifest/path contract for future LSP artifact coordination after the MetadataLoadContext slice stabilizes (discovered in lsp-metadata-loadcontext-r8)

### Flaky Test: TranslationPrewarmServiceTests.DebounceWarmDocument_SupersedesPendingWarm_ForSameFile

**Status**: Not blocking (fails intermittently, passes in isolation)

**Description**: The prewarm debounce test fails sporadically in full test runs due to timing sensitivity. When run individually, it consistently passes. This is a pre-existing timing issue in the prewarming service's debounce logic, not related to v2 extraction work.

**Severity**: Low — test logic is sound, but environment/timing needs investigation

**Deferral**: This should be split into focused unit tests for debounce logic and integration tests, with configurable timeouts or clock mocking.
