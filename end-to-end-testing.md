# End To End Testing

## Query Extraction V2 Slice 1

1. **Direct DbSet query chain with terminal operator**
   - Open a sample project with EF hover enabled
   - Hover over the terminal operator (e.g., `ToListAsync()`) in a direct query chain: `dbContext.Users.Where(...).ToListAsync()`
   - Expected: Extraction succeeds with materialized boundary classification, hover preview displays SQL

2. **Query expression form (LINQ syntax)**
   - Hover over a query-expression-form query: `from u in dbContext.Users where u.IsActive select u`
   - Expected: Extraction succeeds with queryable boundary classification (no terminal materializer found)

3. **Direct IQueryable helper with additional operators**
   - Create a helper method `IQueryable<T> GetActiveUsers() { return dbContext.Users.Where(u => u.IsActive); }`
   - Hover over a call site: `GetActiveUsers().OrderBy(...).ToListAsync()`
   - Expected: Helper is recognized and inlined, extraction succeeds, hover preview shows full composed query

4. **Multi-expression helper composition**
   - Create helper: `IQueryable<T> Query<T>(Expression<Func<U, bool>> where, Expression<Func<U, T>> select) { return dbContext.Users.Where(where).Select(select); }`
   - Hover over call: `Query(u => u.IsActive, u => u.Name).ToListAsync()`
   - Expected: Multi-expression parameters are recognized, helper is inlined, extraction succeeds

5. **Unsupported helper with control flow**
   - Create helper with if statement: `IQueryable<T> GetUsers(bool active) { if (active) return dbContext.Users.Where(...); return dbContext.Users; }`
   - Hover over call: `GetUsers(true).ToListAsync()`
   - Expected: Extraction fails with explicit diagnostic `QLV2_UNSUPPORTED_HELPER_CONTROL_FLOW` (not silent fallback)

## Query Extraction V2 Slice 2

1. **Helper-composed query with multiple expression parameters**
   - Open a sample project with EF hover enabled
   - Hover over a query that uses helper methods with multiple expression parameters composed into the return query chain
   - Expected: Extraction and capture classification succeed with deterministic symbol ordering, hover preview displays

2. **Query requiring placeholder capture**
   - Hover over a query that requires placeholder capture for unsupported value types
   - Expected: Explicit placeholder classification appears and SQL execution works where supported

3. **Query with unsupported branch-local capture**
   - Hover over a query with branch-local variables or invocation-only capture shapes
   - Expected: Explicit capture diagnostic is shown and query is rejected for execution with actionable error message

## Query Extraction V2 Slice 3b (Runtime Codegen Integration)

1. **Direct terminal chain v2 codegen and SQL execution**
   - Hover over a direct query chain: `db.Users.Where(u => u.IsActive).ToListAsync()`
   - Expected: V2 extraction and capture succeed, codegen emits deterministic initialization code, SQL is returned (parity with legacy path)
   - Verify: VSCode preview pane displays SQL query text

2. **Helper-composed query v2 codegen**
   - Create helper: `IQueryable<T> GetActive<T>(IQueryable<T> q) { return q.Where(u => u.IsActive); }`
   - Hover over: `GetActive(db.Users).OrderBy(u => u.Name).ToListAsync()`
   - Expected: V2 capture plan includes ReplayInitializer for both `q` and `u` parameters, codegen emits correct initialization, SQL reflects both operations
   - Verify: SQL includes both `WHERE` and `ORDER BY` clauses

3. **V2 payload rejection with diagnostic message**
   - Create unsupported helper with control flow (from Slice 1 test #5)
   - Hover over an occurrence after Slice 3b deployment
   - Expected: Extraction fails with explicit diagnostic, no attempt at legacy fallback, error message shown in LSP hover context
   - Verify: Hover preview shows error message with diagnostic code and reason

4. **Non-v2 query paths unchanged (regression check)**
   - Hover over any legacy query pattern not covered by v2 extraction (e.g., complex LINQ expression, custom operators)
   - Expected: Legacy extraction and codegen path still works, SQL is returned, no change to existing behavior
   - Verify: Solution builds with no new errors or warnings in LSP/daemon components

## LSP Metadata LoadContext

1. **First hover during cold start shows explicit status**
   - Open a sample project and trigger a query preview before warmup completes
   - Expected: Preview shows an immediate startup/warmup status message (e.g., "QueryLens starting" or "warming up")
   - Verify: No silent waiting period for user

2. **Preview succeeds after warmup without restart**
   - Wait for warmup and trigger the same preview again
   - Expected: SQL preview succeeds without requiring IDE restart
   - Verify: Transition from startup status to working preview is seamless

3. **No bin-folder DLL locks after metadata inspection**
   - Rebuild the sample project after previewing
   - Expected: Build outputs are not blocked by the LSP process
   - Verify: No "file in use" errors during build

## V2 Production Wiring (Pipeline + VS Code)

1. **Legacy payload path remains transparent**
   - Open `samples/SampleMySqlApp` in VS Code with EF QueryLens extension running
   - Hover over `db.Orders` or `db.Orders.Where(o => o.UserId > 0)`
   - Expected: SQL preview appears successfully with no v2-specific diagnostics

2. **V2-supported query uses capture-plan stubs**
   - Hover over a direct chain eligible for v2 extraction/capture: `db.Orders.Where(o => o.UserId > 0).ToListAsync()`
   - Expected: SQL preview appears and matches the legacy shape for equivalent query
   - Verify: no fallback error message, no `(no v2 diagnostic)` text

3. **V2 rejection surfaces structured diagnostic**
   - Hover over a query shape known to generate capture diagnostics (unsupported outer symbol capture)
   - Expected: hover shows error message including `capture-rejected:` and capture diagnostic detail
   - Verify: no crash, no empty preview pane
