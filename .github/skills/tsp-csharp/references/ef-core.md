# Entity Framework Core

## Purpose

Rules for DbContext lifetime, querying, migrations, change tracking, bulk operations, and performance in EF Core-based applications.

## Default Guidance

### Core Rules (Non-Negotiable)

- **No lazy loading** — never enable `UseLazyLoadingProxies()`. Use eager loading (`Include`) or explicit projection (`Select`)
- **AsNoTracking by default** — use `AsNoTracking()` for all read operations, or set `QueryTrackingBehavior.NoTracking` globally
- **DbContext is scoped** — never cache, share across requests, or register as Singleton. Use `AddDbContextPool` for high-throughput scenarios
- **Fix N+1 queries immediately** — use `Include()`, `AsSplitQuery()`, or projection

### DbContext Design

- Keep DbContext classes focused — one per bounded context if using DDD
- Use constructor injection for `DbContextOptions<T>`
- Separate entity configurations into `IEntityTypeConfiguration<T>` classes
- Use `DbContextFactory` for console apps, background services, or tests

```csharp
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
```

### Entity Design

- Every entity must include audit fields: `CreatedAt`, `ModifiedAt`, `CreatedBy`, `ModifiedBy` — implement via `SaveChangesAsync` override or EF interceptor, not per-entity manual assignment
- Every entity must include `IsDeleted` (soft delete) with a global query filter — strongly prefer over cascade/hard deletes. Cascade delete rules require explicit justification
- Use `decimal` with explicit precision for currency/financial values — **never** `float` or `double`. Configure via `HasPrecision(18, 2)` or `[Precision(18, 2)]`
- Specify `MaxLength` on all string columns — `nvarchar(max)` requires written justification
- No comma/delimiter-separated values in columns — normalize into a join table or use an owned type collection
- JSON columns are appropriate only for externally-sourced data (webhook payloads, audit logs) — not for queryable domain data
- Define foreign keys explicitly with appropriate `DeleteBehavior` (Restrict/SetNull) — justify any Cascade
- Define indexes for frequently filtered/sorted columns; use composite indexes for common query patterns
- Use enums for status fields and other enumerations — never raw strings or magic integers

```csharp
// Recommended: auditable base class + SaveChanges interceptor
public abstract class AuditableEntity
{
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; set; }
    public string CreatedBy { get; init; } = string.Empty;
    public string ModifiedBy { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}

// Entity configuration example
public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.HasQueryFilter(o => !o.IsDeleted);
        builder.Property(o => o.Total).HasPrecision(18, 2);
        builder.Property(o => o.Name).HasMaxLength(200);
        builder.HasIndex(o => o.Status);
        builder.HasIndex(o => new { o.CreatedAt, o.Status });
    }
}
```

### Querying

- Prefer projection (`Select`) over loading full entities when only a subset of data is needed
- Use `Any()` instead of `Count() > 0` for existence checks
- Never call `ToList()` before `Where` — filter in the database, not in memory
- Use `FromSqlInterpolated()` for raw SQL — **never** `FromSqlRaw()` with string concatenation
- Use compiled queries (`EF.CompileAsyncQuery`) for frequently executed hot-path queries
- Always pass `CancellationToken` to all async EF methods

```csharp
var summaries = await context.Orders
    .AsNoTracking()
    .Where(o => o.Status == OrderStatus.Active)
    .Select(o => new OrderSummary(o.Id, o.Total, o.CreatedAt))
    .ToListAsync(cancellationToken);
```

### Pagination

- Every endpoint returning a list **must** paginate — no unbounded `ToListAsync()`
- Use `Skip`/`Take` with a default page size and a configurable max cap
- Return total count only when the client needs it (requires a separate `COUNT` query — expensive on large tables)
- Standardize on a `PagedResult<T>` shape across the project

```csharp
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

// Usage
var query = context.Orders
    .AsNoTracking()
    .Where(o => o.Status == OrderStatus.Active)
    .OrderBy(o => o.CreatedAt);

var totalCount = await query.CountAsync(cancellationToken);
var items = await query
    .Skip((page - 1) * pageSize)
    .Take(pageSize)
    .Select(o => new OrderSummary(o.Id, o.Total, o.CreatedAt))
    .ToListAsync(cancellationToken);

return new PagedResult<OrderSummary>(items, totalCount, page, pageSize);
```

### Bulk Operations (EF Core 7+)

- Use `ExecuteUpdateAsync()` / `ExecuteDeleteAsync()` instead of fetch-then-modify
- These bypass change tracking and execute as a single SQL statement

```csharp
await context.Orders
    .Where(o => o.Status == OrderStatus.Expired)
    .ExecuteDeleteAsync(cancellationToken);
```

### Change Tracking & Saving

- Batch `SaveChangesAsync()` calls — don't call per-entity
- Implement concurrency control with `[ConcurrencyCheck]` or row version tokens
- Use explicit transactions when multiple SaveChanges calls must be atomic
- Use execution strategies for retryable transactions

```csharp
var strategy = db.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    // ... operations ...
    await transaction.CommitAsync(ct);
});
```

### Migrations

- Create small, focused migrations with descriptive names
- Review generated SQL before applying to production (`dotnet ef migrations script`)
- Never manually edit migration files after they've been applied
- Use data seeding through migrations only for reference data

### Performance Monitoring

- Enable SQL logging in development (`LogTo` or `appsettings.json`)
- Watch for: N+1 queries, client-side evaluation warnings, missing indexes, large unpaginated result sets

## Avoid

| Anti-Pattern | Fix |
| --- | --- |
| Lazy loading enabled | Remove `UseLazyLoadingProxies()`, use `Include` or `Select` |
| `ToList()` before `Where` | Move `Where` before materialization |
| `Count() > 0` | Use `Any()` |
| `FromSqlRaw` with concatenation | Use `FromSqlInterpolated` |
| Singleton DbContext | Register as Scoped or use `AddDbContextPool` |
| Fetch-then-update for bulk | Use `ExecuteUpdateAsync` / `ExecuteDeleteAsync` |
| No `AsNoTracking` on reads | Add `.AsNoTracking()` or set global default |
| In-memory provider for integration tests | Use SQLite in-memory (has proper relational behavior) |
| Missing `CancellationToken` in queries | Pass `cancellationToken` to all `*Async` methods |
| Unbounded list query (no pagination) | Add `Skip`/`Take` with default and max page size |
| `float`/`double` for currency | `decimal` with `HasPrecision(18, 2)` |
| `nvarchar(max)` without justification | Specify `MaxLength` on all string columns |
| Missing audit fields on entity | `CreatedAt`, `ModifiedAt`, `CreatedBy`, `ModifiedBy`, `IsDeleted` on every entity |
| Hard/cascade delete without justification | Soft delete with `IsDeleted` + global query filter |
| Comma-delimited values in columns | Normalize to join table or owned collection |

## Review Checklist

- [ ] Context lifetime and ownership are clear (scoped per request or unit of work)
- [ ] Read queries use `AsNoTracking()` or global default
- [ ] No lazy loading configured
- [ ] Queries project only needed data (use `Select` over full entity loads)
- [ ] N+1 patterns addressed with `Include`, `AsSplitQuery`, or projection
- [ ] Raw SQL uses `FromSqlInterpolated`, never concatenation
- [ ] Bulk updates use `ExecuteUpdateAsync` / `ExecuteDeleteAsync`
- [ ] `CancellationToken` passed to all async EF methods
- [ ] Tests use SQLite in-memory, not `UseInMemoryDatabase`
- [ ] Entities include audit fields (`CreatedAt`, `ModifiedAt`, `CreatedBy`, `ModifiedBy`, `IsDeleted`)
- [ ] Soft delete configured with global query filter
- [ ] Currency fields use `decimal` with explicit precision — no `float`/`double`
- [ ] String columns have `MaxLength` — no `nvarchar(max)` without justification
- [ ] Indexes defined for frequently queried columns
- [ ] List endpoints return paginated results with default and max page size
- [ ] No comma-delimited values or unstructured JSON for queryable domain data

## Related Files

- [Async](./async.md) — CancellationToken threading, async query patterns
- [Performance](./perf.md) — compiled queries, streaming with IAsyncEnumerable
- [Security](./security.md) — parameterized queries, SQL injection prevention

## Source Anchors

- [EF Core documentation hub](https://learn.microsoft.com/en-us/ef/core/)
- [Efficient querying](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying)
- [Handling concurrency conflicts](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)
- [Connection resiliency](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency)
- [ExecuteUpdate and ExecuteDelete](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete)
