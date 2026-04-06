using System.Reflection;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Tests.Lsp.Fakes;
using EFQueryLens.Lsp.Services;

namespace EFQueryLens.Core.Tests.Lsp;

public class HoverPreviewServiceFormattingTests : IDisposable
{
    private readonly string? _originalClient = Environment.GetEnvironmentVariable("QUERYLENS_CLIENT");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("QUERYLENS_CLIENT", _originalClient);
    }

    [Fact]
    public void BuildFormattedStatements_AddsSplitLabels_ForMultipleCommands()
    {
        var method = GetStaticMethod("BuildFormattedStatements", typeof(IReadOnlyList<QuerySqlCommand>), typeof(string));

        var commands = (IReadOnlyList<QuerySqlCommand>)
        [
            new QuerySqlCommand { Sql = "select 1" },
            new QuerySqlCommand { Sql = "select 2" },
        ];

        var result = (IReadOnlyList<QueryLensSqlStatement>)method.Invoke(null, [commands, "SqlServer"])!;

        Assert.Equal(2, result.Count);
        Assert.Equal("Split Query 1 of 2", result[0].SplitLabel);
        Assert.Equal("Split Query 2 of 2", result[1].SplitLabel);
        Assert.Contains("SELECT", result[0].Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStatementsSqlBlock_IncludesSplitLabels()
    {
        var method = GetStaticMethod("BuildStatementsSqlBlock", typeof(IReadOnlyList<QueryLensSqlStatement>));

        IReadOnlyList<QueryLensSqlStatement> statements =
        [
            new("SELECT 1", "Split Query 1 of 2"),
            new("SELECT 2", null),
        ];

        var sql = (string)method.Invoke(null, [statements])!;

        Assert.Contains("-- Split Query 1 of 2", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT 2", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWarningLines_FormatsWithAndWithoutSuggestion()
    {
        var method = GetStaticMethod("BuildWarningLines", typeof(IReadOnlyList<QueryWarning>));

        IReadOnlyList<QueryWarning> warnings =
        [
            new QueryWarning { Severity = WarningSeverity.Warning, Code = "QL1", Message = "M1", Suggestion = "S1" },
            new QueryWarning { Severity = WarningSeverity.Info, Code = "QL2", Message = "M2" },
        ];

        var lines = (IReadOnlyList<string>)method.Invoke(null, [warnings])!;

        Assert.Equal(2, lines.Count);
        Assert.Contains("QL1: M1 (S1)", lines[0], StringComparison.Ordinal);
        Assert.Contains("QL2: M2", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHoverMarkdown_EmitsActionLinks_ForNonRiderClient()
    {
        Environment.SetEnvironmentVariable("QUERYLENS_CLIENT", "vscode");
        var service = new HoverPreviewService(new TestControllableEngine());

        var method = GetInstanceMethod(
            "BuildHoverMarkdown",
            typeof(IReadOnlyList<QuerySqlCommand>),
            typeof(IReadOnlyList<QueryWarning>),
            typeof(TranslationMetadata),
            typeof(double),
            typeof(string),
            typeof(int),
            typeof(int));

        IReadOnlyList<QuerySqlCommand> commands = [new QuerySqlCommand { Sql = "select 1" }];
        IReadOnlyList<QueryWarning> warnings = [];

        var markdown = (string)method.Invoke(service, [
            commands,
            warnings,
            new TranslationMetadata { DbContextType = "MyDb", EfCoreVersion = "9", ProviderName = "SqlServer", TranslationTime = TimeSpan.FromMilliseconds(1) },
            12.0,
            Path.GetFullPath("test.cs"),
            10,
            5,
        ])!;

        Assert.Contains("Copy SQL", markdown, StringComparison.Ordinal);
        Assert.Contains("Open SQL", markdown, StringComparison.Ordinal);
        Assert.Contains("Reanalyze", markdown, StringComparison.Ordinal);
        Assert.Contains("SQL generation time 12 ms", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHoverMarkdown_OmitsLinks_ForRiderClient_AndIncludesHint()
    {
        Environment.SetEnvironmentVariable("QUERYLENS_CLIENT", "rider");
        var service = new HoverPreviewService(new TestControllableEngine());

        var method = GetInstanceMethod(
            "BuildHoverMarkdown",
            typeof(IReadOnlyList<QuerySqlCommand>),
            typeof(IReadOnlyList<QueryWarning>),
            typeof(TranslationMetadata),
            typeof(double),
            typeof(string),
            typeof(int),
            typeof(int));

        IReadOnlyList<QuerySqlCommand> commands = [new QuerySqlCommand { Sql = "select 1" }];
        IReadOnlyList<QueryWarning> warnings = [];

        var markdown = (string)method.Invoke(service, [
            commands,
            warnings,
            new TranslationMetadata { DbContextType = "MyDb", EfCoreVersion = "9", ProviderName = "SqlServer", TranslationTime = TimeSpan.FromMilliseconds(1) },
            0.0,
            Path.GetFullPath("test.cs"),
            10,
            5,
        ])!;

        Assert.DoesNotContain("Copy SQL", markdown, StringComparison.Ordinal);
        Assert.Contains("Alt+Enter", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStructuredEnrichedSql_IncludesMetadataExpressionsWarningsAndParameters()
    {
        var method = GetStaticMethod(
            "BuildStructuredEnrichedSql",
            typeof(string),
            typeof(string),
            typeof(int),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(IReadOnlyList<QueryWarning>),
            typeof(bool),
            typeof(IReadOnlyList<QueryParameter>));

        IReadOnlyList<QueryWarning> warnings =
        [
            new QueryWarning { Severity = WarningSeverity.Warning, Code = "QLX", Message = "Warn", Suggestion = "Fix" },
        ];

        IReadOnlyList<QueryParameter> parameters =
        [
            new QueryParameter { Name = "@p0", ClrType = "System.Int32", InferredValue = "5" },
        ];

        var enriched = (string?)method.Invoke(null, [
            "SELECT * FROM Orders",
            "file.cs",
            42,
            "db.Orders",
            "db.Orders.Where(o => o.Id > 0)",
            "9.0.0",
            "My.Namespace.MyDbContext",
            "SqlServer",
            warnings,
            true,
            parameters,
        ]);

        Assert.NotNull(enriched);
        Assert.Contains("# EF QueryLens", enriched, StringComparison.Ordinal);
        Assert.Contains("- Source:", enriched, StringComparison.Ordinal);
        Assert.Contains("- EF Core:", enriched, StringComparison.Ordinal);
        Assert.Contains("- DbContext:", enriched, StringComparison.Ordinal);
        Assert.Contains("- Provider:", enriched, StringComparison.Ordinal);
        Assert.Contains("## LINQ (csharp)", enriched, StringComparison.Ordinal);
        Assert.Contains("## Executed LINQ (csharp)", enriched, StringComparison.Ordinal);
        Assert.Contains("```csharp", enriched, StringComparison.Ordinal);
        Assert.Contains("## Parameters", enriched, StringComparison.Ordinal);
        Assert.Contains("## SQL", enriched, StringComparison.Ordinal);
        Assert.Contains("```sql", enriched, StringComparison.Ordinal);
        Assert.Contains("Client evaluation", enriched, StringComparison.Ordinal);
        Assert.Contains("QLX: Warn", enriched, StringComparison.Ordinal);
        Assert.Contains("SELECT * FROM Orders", enriched, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStructuredEnrichedSql_ReturnsNull_ForEmptySql()
    {
        var method = GetStaticMethod(
            "BuildStructuredEnrichedSql",
            typeof(string),
            typeof(string),
            typeof(int),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(IReadOnlyList<QueryWarning>),
            typeof(bool),
            typeof(IReadOnlyList<QueryParameter>));

        var enriched = method.Invoke(null, [
            " ",
            "file.cs",
            1,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
        ]);

        Assert.Null(enriched);
    }

    [Fact]
    public void BuildStructuredEnrichedSql_ExecutedLinqStatementSnippet_PreservesLayout()
    {
        var method = GetStaticMethod(
            "BuildStructuredEnrichedSql",
            typeof(string),
            typeof(string),
            typeof(int),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(IReadOnlyList<QueryWarning>),
            typeof(bool),
            typeof(IReadOnlyList<QueryParameter>));

        var executed = "var x = 10;\nvar custom = new\n{\n\n};\n\nquery = db.q;\nquery.Where(x).ToList();";

        var enriched = (string?)method.Invoke(null, [
            "SELECT 1",
            "file.cs",
            10,
            "db.Orders",
            executed,
            "9.0.0",
            "My.Namespace.MyDbContext",
            "SqlServer",
            null,
            false,
            null,
        ]);

        Assert.NotNull(enriched);
        var normalized = enriched.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("## Executed LINQ (csharp)", normalized, StringComparison.Ordinal);
        Assert.Contains("var x = 10;", normalized, StringComparison.Ordinal);
        Assert.Contains("var custom = new", normalized, StringComparison.Ordinal);
        Assert.Contains("query = db.q;", normalized, StringComparison.Ordinal);
        Assert.Contains("query.Where(x).ToList();", normalized, StringComparison.Ordinal);
        Assert.Contains("\n\n};\n\nquery", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStructuredEnrichedSql_StripsSyntheticVarUsageComments_FromExpressionBlock()
    {
        var method = GetStaticMethod(
            "BuildStructuredEnrichedSql",
            typeof(string),
            typeof(string),
            typeof(int),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(IReadOnlyList<QueryWarning>),
            typeof(bool),
            typeof(IReadOnlyList<QueryParameter>));

        var sourceExpression = """
            // var customerId: var (used 9x)
            // var CustomerId: Guid (used 6x)
            _dbContext.Orders.Where(o => o.Customer.CustomerId == customerId)
            """;

        var enriched = (string?)method.Invoke(null, [
            "SELECT 1",
            "file.cs",
            5,
            sourceExpression,
            null,
            "9.0.0",
            "My.Namespace.MyDbContext",
            "SqlServer",
            null,
            false,
            null,
        ]);

        Assert.NotNull(enriched);
        Assert.DoesNotContain("// var customerId: var (used 9x)", enriched, StringComparison.Ordinal);
        Assert.DoesNotContain("// var CustomerId: Guid (used 6x)", enriched, StringComparison.Ordinal);
        Assert.Contains("_dbContext", enriched, StringComparison.Ordinal);
        Assert.Contains(".Orders", enriched, StringComparison.Ordinal);
        Assert.Contains("customerId", enriched, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeCodeForComment_LongFluentChain_FormatsAsMultiline()
    {
        var method = GetStaticMethod("NormalizeCodeForComment", typeof(string));

        var expression = "db.Orders.Where(o => o.IsNotDeleted).Where(o => o.Total > 10).OrderByDescending(o => o.CreatedUtc).ThenBy(o => o.Id).Take(10).Select(o => o.Id)";

        var formatted = (string)method.Invoke(null, [expression])!;

        Assert.Contains("db.Orders", formatted, StringComparison.Ordinal);
        Assert.Contains(".Where(o => o.IsNotDeleted)", formatted, StringComparison.Ordinal);
        Assert.Contains(".OrderByDescending(o => o.CreatedUtc)", formatted, StringComparison.Ordinal);
        Assert.Contains(".ThenBy(o => o.Id)", formatted, StringComparison.Ordinal);
        Assert.Contains(".Select(o => o.Id)", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeCodeForComment_TernaryExpression_FormatsAsMultiline()
    {
        var method = GetStaticMethod("NormalizeCodeForComment", typeof(string));

        var expression = "x > 0 ? db.Orders.Where(o => o.IsNotDeleted) : db.Orders.Where(o => o.Total > 10)";

        var formatted = (string)method.Invoke(null, [expression])!;

        Assert.Contains("x > 0", formatted, StringComparison.Ordinal);
        Assert.Contains("? db.Orders.Where(o => o.IsNotDeleted)", formatted, StringComparison.Ordinal);
        Assert.Contains(": db.Orders.Where(o => o.Total > 10)", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MySql", "select * from orders")]
    [InlineData("Npgsql", "select * from orders")]
    [InlineData("SqlServer", "select * from orders")]
    [InlineData(null, "select * from orders")]
    public void FormatSqlForDisplay_ReturnsReadableSql_ForDifferentDialects(string? provider, string sql)
    {
        var method = GetStaticMethod("FormatSqlForDisplay", typeof(string), typeof(string));

        var formatted = (string)method.Invoke(null, [sql, provider])!;

        Assert.False(string.IsNullOrWhiteSpace(formatted));
        Assert.Contains("SELECT", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAdditionalImports_DedupesAndAlwaysAddsSystemLinq()
    {
        var method = GetStaticMethod("BuildAdditionalImports", typeof(IEnumerable<string>));

        var imports = (IReadOnlyList<string>)method.Invoke(null, [(IEnumerable<string>)["System", "System", "System.Linq", "Custom"]])!;

        Assert.Equal(3, imports.Count);
        Assert.Contains("System.Linq", imports, StringComparer.Ordinal);
        Assert.Contains("Custom", imports, StringComparer.Ordinal);
    }

    [Fact]
    public void ResolveExecutedExpression_WhenTranslatorDoesNotRewrite_UsesSynthesizedExpressionForHelperCalls()
    {
        var method = GetStaticMethod("ResolveExecutedExpression", typeof(string), typeof(string), typeof(string));

        var executed = (string?)method.Invoke(null, [
            "service.GetOrdersAsync(customerId, whereExpression, selector, ct)",
            "dbContext.Orders.Where(o => o.CustomerId == customerId).Where(o => !o.IsDeleted).Select(selector).ToListAsync(ct)",
            null,
        ]);

        Assert.NotNull(executed);
        Assert.Contains("dbContext.Orders", executed, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveExecutedExpression_PrefersTranslatorRewrite_WhenAvailable()
    {
        var method = GetStaticMethod("ResolveExecutedExpression", typeof(string), typeof(string), typeof(string));

        var executed = (string?)method.Invoke(null, [
            "sourceExpr",
            "synthesizedExpr",
            "rewrittenExpr",
        ]);

        Assert.Equal("rewrittenExpr", executed);
    }

    [Fact]
    public void FindUnresolvedSymbols_IgnoresNamedArgumentIdentifier()
    {
        var method = GetStaticMethod(
            "FindUnresolvedSymbols",
            typeof(string),
            typeof(string),
            typeof(IReadOnlyList<LocalSymbolGraphEntry>));

        IReadOnlyList<LocalSymbolGraphEntry> graph =
        [
            new()
            {
                Name = "ct",
                TypeName = "global::System.Threading.CancellationToken",
                Kind = "parameter",
                DeclarationOrder = 1,
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
        ];

        var unresolved = (IReadOnlyList<string>)method.Invoke(null, [
            "dbContext.Companies.SingleOrDefaultAsync(cancellationToken: ct)",
            "dbContext",
            graph,
        ])!;

        Assert.DoesNotContain("cancellationToken", unresolved, StringComparer.Ordinal);
    }

    [Fact]
    public void FindUnresolvedSymbols_IgnoresQueryExpressionRangeVariable()
    {
        var method = GetStaticMethod(
            "FindUnresolvedSymbols",
            typeof(string),
            typeof(string),
            typeof(IReadOnlyList<LocalSymbolGraphEntry>));

        IReadOnlyList<LocalSymbolGraphEntry> graph =
        [
            new()
            {
                Name = "fromUtc",
                TypeName = "global::System.DateTime",
                Kind = "local",
                DeclarationOrder = 1,
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
            new()
            {
                Name = "ct",
                TypeName = "global::System.Threading.CancellationToken",
                Kind = "parameter",
                DeclarationOrder = 2,
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
        ];

        var unresolved = (IReadOnlyList<string>)method.Invoke(null, [
            "(from o in dbContext.Orders where o.IsNotDeleted && o.CreatedUtc >= fromUtc orderby o.CreatedUtc descending select o.Id).ToListAsync(ct)",
            "dbContext",
            graph,
        ])!;

        Assert.DoesNotContain("o", unresolved, StringComparer.Ordinal);
        Assert.DoesNotContain("fromUtc", unresolved, StringComparer.Ordinal);
    }

    [Fact]
    public void FindUnresolvedSymbols_IgnoresSyntheticFactoryReceiver()
    {
        var method = GetStaticMethod(
            "FindUnresolvedSymbols",
            typeof(string),
            typeof(string),
            typeof(IReadOnlyList<LocalSymbolGraphEntry>));

        IReadOnlyList<LocalSymbolGraphEntry> graph =
        [
            new()
            {
                Name = "ct",
                TypeName = "global::System.Threading.CancellationToken",
                Kind = "parameter",
                DeclarationOrder = 1,
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            },
        ];

        var unresolved = (IReadOnlyList<string>)method.Invoke(null, [
            "__qlFactoryContext.Rationales.AsNoTracking().OrderBy(x => x.Title).ToListAsync(ct)",
            "Rationales",
            graph,
        ])!;

        Assert.DoesNotContain("__qlFactoryContext", unresolved, StringComparer.Ordinal);
    }

    private static MethodInfo GetStaticMethod(string name, params Type[] parameterTypes) =>
        typeof(HoverPreviewService).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, parameterTypes, null)
        ?? throw new InvalidOperationException($"Method {name} not found.");

    private static MethodInfo GetInstanceMethod(string name, params Type[] parameterTypes) =>
        typeof(HoverPreviewService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, parameterTypes, null)
        ?? throw new InvalidOperationException($"Method {name} not found.");
}
