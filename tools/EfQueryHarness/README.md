# EF Query Harness

Minimal harness for extracting actual SQL from LINQ expressions compiled via Entity Framework Core.

## Purpose

The query harness enables deterministic validation of generated SQL by:
1. Accepting a compiled query expression
2. Passing it through EF Core's query translation pipeline
3. Extracting the generated SQL using EF's internal APIs
4. Returning the SQL for validation by query analysis and runtime tests

## Usage

```bash
dotnet run --project tools/EfQueryHarness/EfQueryHarness.csproj -- "<query-expression>"
```

## Status

**Skeleton implementation** — Created for slice 1 as a placeholder. Full implementation deferred to slice 3 (runtime) where SQL validation is needed.

### Future Work (Slice 3)

- Implement query compilation and EF Core translation
- Extract generated SQL using `IRelationalCommandCache` or equivalent
- Support multiple database providers (SQL Server, PostgreSQL, SQLite, MySQL)
- Return structured SQL output for downstream validation

## Design Notes

- The harness is a standalone console app to avoid embedding EF Core directly in the LSP server
- Query expressions are passed as strings to allow dynamic query building
- Output is pure SQL suitable for EXPLAIN plan analysis or regression testing
