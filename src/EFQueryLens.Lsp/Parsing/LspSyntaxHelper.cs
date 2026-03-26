using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EFQueryLens.Lsp.Parsing;

public sealed record SourceUsingContext(
    IReadOnlyList<string> Imports,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<string> StaticTypes);

public sealed record LinqChainInfo(
    string Expression,
    string ContextVariableName,
    string DbSetMemberName,
    /// <summary>Line/character of the hover anchor (end of query) for opening doc / LSP hover.</summary>
    int Line,
    int Character,
    int EndLine,
    int EndCharacter,
    /// <summary>Line/character where the CodeLens badge is drawn (above the statement).</summary>
    int BadgeLine,
    int BadgeCharacter,
    /// <summary>Full statement span: hover doc is shown when caret is anywhere in this range.</summary>
    int StatementStartLine,
    int StatementStartCharacter,
    int StatementEndLine,
    int StatementEndCharacter);

public static partial class LspSyntaxHelper
{
    private static readonly HashSet<string> TerminalMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ToList", "ToListAsync", "ToArray", "ToArrayAsync", "ToDictionary", "ToDictionaryAsync",
        "First", "FirstOrDefault", "FirstAsync", "FirstOrDefaultAsync",
        "Single", "SingleOrDefault", "SingleAsync", "SingleOrDefaultAsync",
        "Last", "LastOrDefault", "LastAsync", "LastOrDefaultAsync",
        "Count", "CountAsync", "LongCount", "LongCountAsync",
        "Any", "AnyAsync", "All", "AllAsync",
        "Min", "MinAsync", "Max", "MaxAsync", "Sum", "SumAsync", "Average", "AverageAsync",
        "ElementAt", "ElementAtOrDefault", "ElementAtAsync", "ElementAtOrDefaultAsync",
        "AsEnumerable", "AsAsyncEnumerable", "ToLookup", "ToLookupAsync",
        "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync"
    };

    private static readonly HashSet<string> QueryChainMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Where", "Select", "SelectMany", "Join", "GroupBy", "OrderBy", "OrderByDescending",
        "ThenBy", "ThenByDescending", "Skip", "Take", "Distinct", "Include", "ThenInclude",
        "AsNoTracking", "AsTracking", "AsSplitQuery", "AsSingleQuery", "Expressionify"
    };

    // Methods that only exist in EF Core — not in System.Linq for in-memory collections.
    // A chain with any of these is definitely an EF query even if the root variable
    // doesn't have a recognisable DbContext name (e.g. a repo wrapper or injected IQueryable).
    private static readonly HashSet<string> EfSpecificMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Include", "ThenInclude",
        "AsNoTracking", "AsNoTrackingWithIdentityResolution", "AsTracking",
        "AsSplitQuery", "AsSingleQuery",
        "TagWith", "TagWithCallSite",
        "IgnoreQueryFilters", "IgnoreAutoIncludes",
        "FromSqlRaw", "FromSqlInterpolated", "FromSql",
        "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync",
        "Load", "LoadAsync",
    };
}
