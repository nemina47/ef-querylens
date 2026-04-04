---
name: tsp-csharp
description: "Comprehensive C# and .NET development skill for TSP projects. Use when writing, reviewing, or debugging C# code — covers async/await, EF Core, performance, security, HTTP resilience, Options pattern, middleware, logging, and ASP.NET Core patterns. Trigger for: async, Task, CancellationToken, DbContext, LINQ, Span, Memory, hot path, authentication, authorization, validation, secrets, HttpClient, IOptions, middleware, minimal API, TypedResults, ProblemDetails, Channels, ILogger, Serilog, logging, pagination, entity design, audit fields, OpenAPI."
---

# C# & .NET — TSP Standards

Target .NET 8+ / C# 12+. Use the reference files below for detailed guidance per domain.

## Reference Files

Read the relevant reference(s) based on the current task:

| When working with... | Read |
|---|---|
| Async/await, Tasks, CancellationToken, IAsyncEnumerable, Channels | [Async & Concurrency](./references/async.md) |
| DbContext, LINQ queries, migrations, change tracking, N+1 | [Entity Framework Core](./references/ef-core.md) |
| Hot paths, Span/Memory, pooling, allocations, benchmarking | [Performance](./references/perf.md) |
| Auth, secrets, input validation, CORS, rate limiting, middleware | [Security](./references/security.md) |
| Structured logging, Serilog, correlation, severity, masking | [Logging](./references/logging.md) |

## Decision Tables

### Result vs Exception

| Error type | Expected? | Use |
|---|---|---|
| Domain / business rule | Yes | `Result<T>` / `ErrorOr<T>` |
| Domain / business rule | No | Exception |
| Infrastructure (DB, network) | — | Exception |

### IOptions Selection

| Need runtime reload? | Per-request scope? | Use |
|---|---|---|
| No | — | `IOptions<T>` (most common) |
| Yes | Yes | `IOptionsSnapshot<T>` |
| Yes | No (live + `OnChange`) | `IOptionsMonitor<T>` |

### Service Lifetime

| Scenario | Lifetime |
|---|---|
| Default for most services | **Scoped** |
| Stateless utilities, factories | Transient |
| Thread-safe caches, channels, singletons by design | Singleton |
| Multiple implementations of same interface | Keyed services (.NET 8+) |

## Default Patterns

| Pattern | Default | Escalate to |
|---|---|---|
| Web entrypoint | Minimal APIs with `TypedResults` | Controllers when filters/conventions help |
| HTTP results | `TypedResults.Ok()` + `ProblemDetails` | Result envelope only when API needs stable error shape |
| Configuration | Options pattern + `ValidateOnStart()` | `IValidateOptions<T>` for cross-property validation |
| Outbound HTTP | Typed clients + `AddStandardResilienceHandler()` | Custom retry/circuit-breaker only when measured |
| Error handling | `ProblemDetails` (RFC 9457) + `UseExceptionHandler()` | `ErrorOr<T>` when failure is expected, not exceptional |
| Concurrency | `Channel<T>` for producer/consumer with backpressure | SemaphoreSlim, other primitives when channels don't fit |
| Health checks | `AddHealthChecks()` with liveness + readiness probes | Custom checks for external dependencies |
| Logging | Serilog + message templates + correlation IDs | Custom enrichers/sinks for specialized needs |

## API Endpoint Conventions

- Dedicated request and response models per endpoint — never bind directly to entities
- Explicit parameter binding: `[FromQuery]`, `[FromBody]`, `[FromRoute]` — no ambiguous model binding
- OpenAPI annotations on every endpoint: `Produces<T>()` / `[ProducesResponseType]` for all response codes
- Use enums for status fields and other enumerations — never raw strings or magic integers
- No business logic in endpoint methods — delegate to services
- Return `ProblemDetails` for all error responses (4xx and 5xx)

## Anti-Patterns Quick Reference

| Anti-Pattern | Fix |
|---|---|
| `new HttpClient()` per request | `IHttpClientFactory` or typed client |
| Manual Polly configuration | `AddStandardResilienceHandler()` |
| `Results.Ok()` | `TypedResults.Ok()` (explicit contract) |
| Missing `ValidateOnStart()` | Always add to Options registration |
| Singleton capturing Scoped | `IServiceScopeFactory` |
| `.Result` / `.Wait()` in async | `await` |
| Exceptions for flow control | `ErrorOr<T>` / Result pattern |
| `DateTime.Now` | `DateTime.UtcNow` or `TimeProvider` |
| `Console.WriteLine` | `ILogger<T>` |
| Silent catch blocks | Log + rethrow or handle explicitly |

## Middleware Ordering (ASP.NET Core)

Order matters. Follow this sequence:

```
1. UseExceptionHandler()          // Catch everything downstream
2. UseStatusCodePages()
3. UseHttpsRedirection()
4. UseStaticFiles()               // If applicable
5. UseRouting()                   // Before auth
6. UseCors()                      // After routing, before auth
7. UseRateLimiter()
8. UseAuthentication()            // Identify user
9. UseAuthorization()             // Check permissions
10. UseOutputCache()              // After auth
11. MapEndpoints()                // Last
```

## Reference Contract

All reference files follow a consistent structure. See [_contract.md](./references/_contract.md) for details.
