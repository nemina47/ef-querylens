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
        Assert.Contains("-- EF QueryLens", enriched, StringComparison.Ordinal);
        Assert.Contains("-- Source:", enriched, StringComparison.Ordinal);
        Assert.Contains("-- EF Core:", enriched, StringComparison.Ordinal);
        Assert.Contains("-- DbContext:", enriched, StringComparison.Ordinal);
        Assert.Contains("-- Provider:", enriched, StringComparison.Ordinal);
        Assert.Contains("-- LINQ:", enriched, StringComparison.Ordinal);
        Assert.Contains("-- Executed LINQ:", enriched, StringComparison.Ordinal);
        Assert.Contains("-- Parameters:", enriched, StringComparison.Ordinal);
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

    private static MethodInfo GetStaticMethod(string name, params Type[] parameterTypes) =>
        typeof(HoverPreviewService).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, parameterTypes, null)
        ?? throw new InvalidOperationException($"Method {name} not found.");

    private static MethodInfo GetInstanceMethod(string name, params Type[] parameterTypes) =>
        typeof(HoverPreviewService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, parameterTypes, null)
        ?? throw new InvalidOperationException($"Method {name} not found.");
}
