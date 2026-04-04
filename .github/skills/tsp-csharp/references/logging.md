# Structured Logging

## Purpose

Rules for structured logging with message templates, log severity, correlation, sensitive data masking, and diagnostic conventions in .NET applications.

## Default Guidance

### Library & Setup

- Use **Serilog** as the recommended logging library — configure in `Program.cs` with `UseSerilog()`
- Use `ILogger<T>` for injection — never `Console.WriteLine`, `Debug.WriteLine`, or `Trace.WriteLine`
- Configure sinks per environment: console (dev), structured JSON (staging/prod), centralized log service (prod)
- Do not mix logging libraries within a project

### Message Templates (Not String Interpolation)

- Always use message templates with named placeholders — **never** string interpolation (`$""`)
- Message templates enable structured querying and avoid unnecessary string allocations
- Use PascalCase for property names in templates: `{OrderId}`, not `{orderId}` or `{order_id}`

```csharp
// Good: structured message template
logger.LogInformation("Order {OrderId} placed by {UserId} for {Total:C}", orderId, userId, total);

// Bad: string interpolation — loses structure, always allocates
logger.LogInformation($"Order {orderId} placed by {userId} for {total:C}");
```

### Log Severity

| Level | When to use |
| --- | --- |
| `Trace` | Fine-grained diagnostic info (disabled in production) |
| `Debug` | Internal state useful during development |
| `Information` | Business events, request/response lifecycle milestones |
| `Warning` | Unexpected but recoverable situations (retry, fallback, degraded) |
| `Error` | Operation failures that need investigation (include exception) |
| `Critical` | Application-wide failures (startup crash, data corruption, unrecoverable) |

- Default minimum level: `Information` in production, `Debug` in development
- Never log at `Information` level inside tight loops — use `Debug` or `Trace`

### Correlation & Traceability

- Include a correlation ID in every request — use `Activity.Current?.TraceId` or middleware-injected `CorrelationId`
- Log entry and exit points of significant operations with a trackable identifier and timing
- Use Serilog enrichers for automatic context: `Enrich.FromLogContext()`, `Enrich.WithMachineName()`, `Enrich.WithThreadId()`
- Push scoped properties with `ILogger.BeginScope()` or `LogContext.PushProperty()`

```csharp
using (logger.BeginScope(new Dictionary<string, object> { ["OrderId"] = orderId }))
{
    logger.LogInformation("Processing order");
    // ... all logs in this scope include OrderId
    logger.LogInformation("Order processed successfully");
}
```

### Sensitive Data

- **Never** log passwords, tokens, API keys, PII (NRIC, email, phone), or financial details
- Use destructuring policies or custom destructurers to mask sensitive fields
- For objects that may contain sensitive data, log only safe projections — not the full object

```csharp
// Bad: logs entire request including sensitive fields
logger.LogInformation("Request: {@Request}", request);

// Good: log only safe fields
logger.LogInformation("Request received for {Endpoint} by {UserId}", endpoint, userId);
```

### Volume & Discipline

- Log **decisions and outcomes**, not mechanics — don't log every method entry/exit by default
- Entry and exit of significant operations (API requests, background jobs, external calls) should be captured with timing
- No excessive logging — if a log line doesn't help diagnose issues, remove it
- Use log level checks for expensive log construction: `if (logger.IsEnabled(LogLevel.Debug))`

## Avoid

| Anti-Pattern | Fix |
| --- | --- |
| `$"Order {orderId} placed"` (interpolation) | `"Order {OrderId} placed", orderId` (message template) |
| `Console.WriteLine` | `ILogger<T>` injection |
| Logging full objects with PII | Log only safe field projections |
| `catch { }` without logging | Log at `Error` level with exception |
| `Information` level in tight loops | Use `Debug` or `Trace` |
| Missing correlation ID | Use `Activity.Current?.TraceId` or middleware |
| Mixing logging libraries | Standardize on Serilog + `ILogger<T>` |
| Logging tokens, passwords, keys | Mask or omit; use destructuring policies |

## Review Checklist

- [ ] All log statements use message templates — no string interpolation
- [ ] Log severity matches the guidance table (no `Information` spam)
- [ ] Correlation ID present in request pipeline
- [ ] No PII, secrets, or tokens in log output
- [ ] Entry/exit of significant operations logged with timing
- [ ] Serilog configured with appropriate sinks per environment
- [ ] Expensive log construction guarded with `IsEnabled` check

## Related Files

- [Security](./security.md) — sensitive data masking, PII handling
- [Async](./async.md) — logging in async contexts, scoped properties
- [Performance](./perf.md) — log level checks to avoid allocation on hot paths

## Source Anchors

- [Logging in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging)
- [Serilog best practices](https://benfoster.io/blog/serilog-best-practices/)
- [High-performance logging](https://learn.microsoft.com/en-us/dotnet/core/extensions/high-performance-logging)
