# Async & Concurrency

## Purpose

Rules for async/await patterns, CancellationToken threading, parallel work, Channels, and IAsyncEnumerable in C# applications.

## Default Guidance

### Naming & Return Types

- All async methods **must** end with `Async` suffix
- Return `Task<T>` when the method returns a value; `Task` when it does not
- Prefer `Task` over `ValueTask` unless performance measurements justify the switch (cache-hit patterns where the method frequently completes synchronously)
- **Never** use `async void` except for event handlers

### Await Discipline

- Always `await` Task-returning methods — no fire-and-forget
- Never use `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` — these cause deadlocks and thread pool starvation
- Use `ConfigureAwait(false)` in library/helper code; omit in application entry points and UI code
- Do not wrap synchronous code in `Task.Run()` just to make it async

### CancellationToken

- Pass `CancellationToken` through **every layer** of the call chain
- Accept `CancellationToken` as the last parameter in all async public APIs
- Call `cancellationToken.ThrowIfCancellationRequested()` inside loops and long-running operations
- For timeouts: create a linked `CancellationTokenSource` with `CancelAfter()`

```csharp
using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
cts.CancelAfter(TimeSpan.FromSeconds(30));
await DoWorkAsync(cts.Token);
```

### Parallel & Concurrent Work

- Use `Task.WhenAll()` for parallel execution — handle `AggregateException` properly
- Use `Task.WhenAny()` for racing tasks or implementing timeouts
- Use `IAsyncEnumerable<T>` with `[EnumeratorCancellation]` for streaming results
- Use `SemaphoreSlim` (not `lock`) for async-compatible synchronization

### Channels (Producer/Consumer)

- Use `Channel<T>` for producer/consumer pipelines with backpressure
- Prefer bounded channels with `BoundedChannelFullMode.Wait` as the default
- Register `ChannelReader<T>` and `ChannelWriter<T>` separately in DI
- Consume with `await foreach (var item in reader.ReadAllAsync(ct))`

```csharp
var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(100)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleWriter = false,
    SingleReader = true
});
```

| Channel FullMode | Behavior                                                    |
| ---------------- | ----------------------------------------------------------- |
| `Wait`           | Producer waits (backpressure) — **recommended default**     |
| `DropNewest`     | Drop incoming item                                          |
| `DropOldest`     | Drop oldest in queue                                        |
| `DropWrite`      | Silently fail write                                         |

### Exception Handling

- Use try/catch around `await` expressions
- Propagate exceptions naturally via `await` — avoid `Task.FromException` except in rare factory methods
- Never swallow exceptions in async methods — always log at the appropriate level
- For `Task.WhenAll`, unwrap `AggregateException` to inspect individual failures

## Avoid

| Anti-Pattern                                   | Fix                                                                |
| ---------------------------------------------- | ------------------------------------------------------------------ |
| `async void` method                            | Change to `async Task` (except event handlers)                     |
| `.Result` or `.Wait()`                         | Use `await` instead                                                |
| Missing `ConfigureAwait(false)` in library     | Add it to all `await` calls in shared/library code                 |
| Fire-and-forget `Task`                         | `await` it, or use a background service with proper error handling |
| `CancellationToken` not threaded through       | Add parameter to API; pass to every async call                     |
| `lock` in async code                           | Replace with `SemaphoreSlim.WaitAsync()`                           |
| Unbounded `Channel` with untrusted producer    | Use bounded channel with backpressure                              |
| `Task.Run()` wrapping sync code                | Keep synchronous methods synchronous                               |

## Review Checklist

- [ ] All async methods end with `Async` suffix
- [ ] `CancellationToken` is accepted and passed through every layer
- [ ] No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` calls
- [ ] Library code uses `ConfigureAwait(false)`
- [ ] `Channel<T>` is bounded with explicit `FullMode` for producer/consumer scenarios
- [ ] Long-running loops call `ThrowIfCancellationRequested()`
- [ ] Parallel work uses `Task.WhenAll` with proper exception handling

## Related Files

- [Performance](./perf.md) — ValueTask vs Task, async overhead reduction
- [EF Core](./ef-core.md) — async query patterns, CancellationToken in data access
- [Security](./security.md) — timeout patterns for external service calls

## Source Anchors

- [Async programming best practices](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/)
- [Task-based asynchronous pattern](https://learn.microsoft.com/en-us/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap)
- [System.Threading.Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)
