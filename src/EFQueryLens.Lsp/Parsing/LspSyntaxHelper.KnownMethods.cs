using System.Collections.Generic;

namespace EFQueryLens.Lsp.Parsing;

/// <summary>
/// Known LINQ and EF Core method names used for query detection and analysis.
/// Organized by category for maintainability.
/// </summary>
public static partial class LspSyntaxHelper
{
    /// <summary>
    /// LINQ terminal methods that end a query chain and return materialized results or scalars.
    /// Examples: ToList, First, Count, Any, etc.
    /// </summary>
    private static readonly HashSet<string> TerminalMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Materialization
        "ToList", "ToListAsync", "ToArray", "ToArrayAsync",
        "ToDictionary", "ToDictionaryAsync", "ToLookup", "ToLookupAsync",
        
        // Element access
        "First", "FirstOrDefault", "FirstAsync", "FirstOrDefaultAsync",
        "Single", "SingleOrDefault", "SingleAsync", "SingleOrDefaultAsync",
        "Last", "LastOrDefault", "LastAsync", "LastOrDefaultAsync",
        "ElementAt", "ElementAtOrDefault", "ElementAtAsync", "ElementAtOrDefaultAsync",
        
        // Aggregation
        "Count", "CountAsync", "LongCount", "LongCountAsync",
        "Any", "AnyAsync", "All", "AllAsync",
        "Min", "MinAsync", "Max", "MaxAsync", "Sum", "SumAsync", "Average", "AverageAsync",
        
        // In-place modification
        "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync",
        
        // Enumerable conversion
        "AsEnumerable", "AsAsyncEnumerable",
    };

    /// <summary>
    /// LINQ methods that continue a query chain, found in System.Linq and EF Core IQueryable.
    /// Examples: Where, Select, Include, etc.
    /// </summary>
    private static readonly HashSet<string> QueryChainMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Filtering & projection
        "Where", "Select", "SelectMany", "Distinct",
        
        // Joining & grouping
        "Join", "GroupBy",
        
        // Ordering
        "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        
        // Pagination
        "Skip", "Take",
        
        // EF Core specific (but grouped here for chain continuation)
        "Include", "ThenInclude",
        "AsNoTracking", "AsTracking", "AsSplitQuery", "AsSingleQuery",
        
        // Custom LINQ extension
        "Expressionify",
    };

    /// <summary>
    /// Methods that only exist in EF Core — not in System.Linq for in-memory collections.
    /// A chain containing any of these is definitely an EF query, even if the root variable
    /// doesn't have a recognizable DbContext name (e.g., a repo wrapper or injected IQueryable).
    /// </summary>
    private static readonly HashSet<string> EfSpecificMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Eager loading
        "Include", "ThenInclude", "Load", "LoadAsync",
        
        // Tracking & split queries
        "AsNoTracking", "AsNoTrackingWithIdentityResolution", "AsTracking",
        "AsSplitQuery", "AsSingleQuery",
        
        // Tagging & filtering
        "TagWith", "TagWithCallSite", "IgnoreQueryFilters", "IgnoreAutoIncludes",
        
        // Raw SQL
        "FromSqlRaw", "FromSqlInterpolated", "FromSql",
        
        // Bulk operations
        "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync",
    };

    /// <summary>
    /// Common variable names used for DbContext instances.
    /// Used for heuristic-based query detection when the DbContext type name is not resolvable.
    /// </summary>
    private static readonly HashSet<string> KnownDbContextRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "db",
        "_db",
        "context",
        "dbContext",
        "_dbContext",
    };
}
