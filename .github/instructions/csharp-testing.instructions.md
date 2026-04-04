---
description: "C# xUnit testing conventions for TSP projects. Use when writing, reviewing, or modifying C# test files."
applyTo: "**/*Tests.cs, **/Tests/**/*.cs, **/*.Tests/**/*.cs"
---

# C# Testing Conventions (xUnit)

- Test project: `{ProjectName}.Tests`
- Test class: `{ClassUnderTest}Tests`
- Test method: `{Method}_{Scenario}_{ExpectedResult}` (e.g., `GetUser_WhenIdIsInvalid_ReturnsNotFound`)
- `[Fact]` for single cases; `[Theory]` + `[InlineData]` / `[ClassData]` for parameterized
- One logical behavior per test (multiple `Assert` calls are fine if testing one concept)
- Use `FluentAssertions` when available in the project
- Strict AAA pattern (Arrange / Act / Assert) with blank line separators between sections
- Tests must run in any order and in parallel — no shared mutable state
- Require tests for every new or changed public API
- Avoid disk I/O in unit tests — use in-memory alternatives or mocks
- Use `ITestOutputHelper` for diagnostic output
- For detailed xUnit patterns (fixtures, data-driven tests, mocking), invoke the `tsp-csharp-xunit` skill
