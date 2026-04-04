# Performance Optimization

## Purpose

Rules for hot-path optimization, allocation reduction, Span/Memory usage, collection performance, streaming, pooling, and benchmarking in C# applications. Apply only when profiling identifies a hot path.

## Default Guidance

**First Principle: Measure before optimizing.** Premature optimization adds complexity without measurable benefit. Only optimize code that profiling identifies as a bottleneck.

### Allocation Reduction

- Use `Span<T>` and `ReadOnlySpan<T>` for slicing arrays/strings without allocation
- Use `Memory<T>` when the span needs to cross async boundaries
- Use `stackalloc` for small, fixed-size buffers (< 256 bytes)
- Prefer `struct` over `class` for small, short-lived value types (< 16 bytes, no boxing)
- Use `ArrayPool<T>.Shared` for temporary buffers instead of `new T[]`
- Use `string.Create()` or `StringBuilder` for complex string building

```csharp
// Good: zero-allocation slice
ReadOnlySpan<char> extension = fileName.AsSpan()[fileName.LastIndexOf('.')..];

// Bad: allocates a new string
string extension = fileName.Substring(fileName.LastIndexOf('.'));
```

### Collection Performance

- Use collection expressions (`[]`) — the compiler optimizes them
- Specify capacity for `List<T>`, `Dictionary<K,V>` when the size is known
- Use `FrozenDictionary<K,V>` / `FrozenSet<T>` (.NET 8+) for read-heavy lookup tables
- Prefer `HashSet<T>` over `List<T>` for contains-checks on large collections
- Avoid LINQ in hot paths — manual loops are faster when allocations matter

### Streaming & I/O

- For large HTTP/file payloads: **stream, never buffer entirely** (`ReadAsStreamAsync`, not `ReadAsStringAsync`)
- Use `System.IO.Pipelines` for high-throughput I/O
- Use `IAsyncEnumerable<T>` to stream results instead of materializing full collections
- Use `JsonSerializer.SerializeAsync` / `DeserializeAsync` with streams

### Object Reuse & Pooling

- Use `ObjectPool<T>` (via `Microsoft.Extensions.ObjectPool`) for expensive-to-create objects
- Use `RecyclableMemoryStream` instead of `MemoryStream` for frequent large allocations
- Reuse `HttpClient` (via `IHttpClientFactory`) — never create/dispose per request

### HTTP Client Performance

- Always use `IHttpClientFactory` or typed clients — never `new HttpClient()` per request
- Use `AddStandardResilienceHandler()` for retry, circuit-breaker, and timeout — avoid manual Polly config
- Stream large responses with `HttpCompletionOption.ResponseHeadersRead` + `ReadAsStreamAsync`
- Customize resilience handler only when measured defaults don't fit

```csharp
builder.Services.AddHttpClient<ICatalogApi, CatalogClient>(client =>
{
    client.BaseAddress = new Uri(config["Catalog:BaseUrl"]!);
})
.AddStandardResilienceHandler();
```

### Async Performance

- Prefer `ValueTask<T>` over `Task<T>` only when the method frequently completes synchronously (cache-hit patterns)
- Avoid `async` overhead for simple pass-through methods — return the `Task` directly
- Use `ConfigureAwait(false)` in library code to avoid unnecessary context capture

### Benchmarking

- Use `BenchmarkDotNet` for microbenchmarks — never `Stopwatch` for perf comparisons
- Profile with `dotnet-trace`, `dotnet-counters`, or Visual Studio profiler
- Focus on: allocations (GC pressure), throughput (ops/sec), and latency (p50/p95/p99)

### Caching

- Cache GET responses sparingly — caching is not a default; add only when measured need exists
- Always set an explicit TTL — never rely on framework/global defaults without justification
- Prefix cache keys with service/domain name to avoid collisions: `orders:summary:{id}`
- Plan the invalidation strategy before adding a cache — stale data is often worse than slow data
- Use `IDistributedCache` for multi-instance deployments; `IMemoryCache` for single-instance only
- Use output caching (`UseOutputCache`) for endpoint-level HTTP response caching
- Cache objects requiring longer lifetime than the global setting need explicit justification

## Avoid

| Anti-Pattern | Fix |
| --- | --- |
| `string + string` in loops | `StringBuilder` or `string.Create` |
| `ToList()` just to call `Count` | Use `Count()` on `IEnumerable` or `Any()` for existence |
| `new HttpClient()` per request | `IHttpClientFactory` or typed client |
| Manual Polly configuration | `AddStandardResilienceHandler()` |
| Boxing value types | Use generics or `Span<T>` |
| LINQ `.ToList().Where()` | Filter before materializing |
| Large `byte[]` allocations | `ArrayPool<byte>.Shared.Rent()` |
| `ReadAsStringAsync` for large payloads | `ReadAsStreamAsync` + stream deserialization |
| `Stopwatch` for benchmarks | `BenchmarkDotNet` |
| Default/unbounded cache TTL | Explicit TTL per cache entry |
| Cache key without service prefix | Prefix: `{service}:{entity}:{id}` |
| Caching without invalidation strategy | Define invalidation before adding cache |

## Review Checklist

- [ ] Hot path identified by profiling, not assumption
- [ ] Allocations minimized (Span, pooling, struct where appropriate)
- [ ] HTTP clients use `IHttpClientFactory` with `AddStandardResilienceHandler()`
- [ ] Large payloads streamed, not buffered
- [ ] Collections sized appropriately or use frozen collections for read-heavy lookups
- [ ] `BenchmarkDotNet` used for any performance claims
- [ ] Cache entries have explicit TTL and prefixed keys
- [ ] Invalidation strategy defined before caching is added

## Related Files

- [Async](./async.md) — ValueTask vs Task, async overhead, ConfigureAwait
- [EF Core](./ef-core.md) — compiled queries, AsNoTracking, projection
- [Security](./security.md) — streaming considerations for large inputs

## Source Anchors

- [Performance best practices](https://learn.microsoft.com/en-us/dotnet/fundamentals/performance/)
- [Memory and Span usage](https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/)
- [Build resilient HTTP apps](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience)
- [IHttpClientFactory](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory)
