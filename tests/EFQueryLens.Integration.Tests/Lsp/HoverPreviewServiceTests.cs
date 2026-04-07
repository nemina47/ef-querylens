using EFQueryLens.Core.Contracts;
using EFQueryLens.Integration.Tests.Lsp.Fakes;
using EFQueryLens.Integration.Tests.Lsp.Fixtures;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Services;

namespace EFQueryLens.Integration.Tests.Lsp;

/// <summary>
/// Tests for <see cref="HoverPreviewService"/> — exercises the early-exit paths
/// that do not require a real engine, assembly, or LSP connection.
/// </summary>
public class HoverPreviewServiceTests : IClassFixture<LspTestFixture>
{
    private readonly LspTestFixture _fixture;

    public HoverPreviewServiceTests(LspTestFixture fixture)
    {
        _fixture = fixture;
    }

    // NoCsprojFile: a path whose parent dir is Path.GetTempPath() (always exists),
    // but no .csproj is found when walking up from there.
    private static readonly string NoCsprojFile =
        Path.Combine(Path.GetTempPath(), "ef-querylens-no-project-test.cs");

    // ── Fast-fail: no LINQ expression at caret ───────────────────────────────

    [Fact]
    public async Task BuildMarkdownAsync_NoLinqExpression_ReturnsFailure()
    {
        var engine = _fixture.CreatePlainEngine();
        var service = new HoverPreviewService(engine);

        // Plain text with no IQueryable chain → expression extraction fails early.
        var result = await service.BuildMarkdownAsync(
            filePath: "Fake.cs",
            sourceText: "var x = 42;",
            line: 0,
            character: 7,
            cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }

    [Fact]
    public async Task BuildStructuredAsync_NoLinqExpression_ReturnsFailureWithEmptyStatements()
    {
        var engine = _fixture.CreatePlainEngine();
        var service = new HoverPreviewService(engine);

        var result = await service.BuildStructuredAsync(
            filePath: "Fake.cs",
            sourceText: "Console.WriteLine(\"hello\");",
            line: 0,
            character: 8,
            cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(result.Statements);
    }

    [Fact]
    public async Task BuildCombinedAsync_NoLinqExpression_BothPartsFail()
    {
        var engine = _fixture.CreatePlainEngine();
        var service = new HoverPreviewService(engine);

        var result = await service.BuildCombinedAsync(
            filePath: "Fake.cs",
            sourceText: "int y = 0;",
            line: 0,
            character: 4,
            cancellationToken: CancellationToken.None);

        Assert.False(result.Markdown.Success);
        Assert.False(result.Structured.Success);
    }

    [Fact]
    public async Task BuildMarkdownAsync_ToQueryStringWrapper_ReturnsGuidanceFailure()
    {
        var engine = _fixture.CreatePlainEngine();
        var service = new HoverPreviewService(engine);

        var sourceText = """
            var sqlItems = service
                .BuildSqlPreviewCatalog(targetCustomerId, DateTime.UtcNow)
                .Select(x => new SqlPreviewItem(x.Title, x.Query.ToQueryString()))
                .ToArray();
            """;

        var result = await service.BuildMarkdownAsync(
            filePath: "Fake.cs",
            sourceText: sourceText,
            line: 2,
            character: 16,
            cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ToQueryString", result.Output, StringComparison.Ordinal);
    }

    // ── Fast-fail: assembly not found ────────────────────────────────────────

    [Theory]
    [InlineData("var q = ctx.Users.Where(u => u.Active);", 0, 8)]
    [InlineData("var q = dbContext.Orders.ToList();", 0, 8)]
    public async Task BuildMarkdownAsync_AssemblyNotFound_ReturnsFailure(
        string sourceText,
        int line,
        int character)
    {
        var engine = _fixture.CreatePlainEngine();
        var service = new HoverPreviewService(engine);

        // Existing path without a project ancestor → resolver fails gracefully.
        var result = await service.BuildMarkdownAsync(
            filePath: NoCsprojFile,
            sourceText: sourceText,
            line: line,
            character: character,
            cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("var q = ctx.Users.Where(u => u.Active);", 0, 8)]
    [InlineData("var q = dbContext.Orders.ToList();", 0, 8)]
    public async Task BuildStructuredAsync_AssemblyNotFound_ReturnsFailureWithEmptyStatements(
        string sourceText,
        int line,
        int character)
    {
        var engine = _fixture.CreatePlainEngine();
        var service = new HoverPreviewService(engine);

        var result = await service.BuildStructuredAsync(
            filePath: NoCsprojFile,
            sourceText: sourceText,
            line: line,
            character: character,
            cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(result.Statements);
    }

    // ── SetDebugEnabled ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetDebugEnabled_True_DoesNotThrow()
    {
        var engine = _fixture.CreatePlainEngine();
        var service = new HoverPreviewService(engine);

        service.SetDebugEnabled(true);

        // Trigger a fast-fail path so the debug logging code is exercised.
        var result = await service.BuildMarkdownAsync(
            filePath: "Debug.cs",
            sourceText: "var x = 0;",
            line: 0,
            character: 0,
            cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public void SetDebugEnabled_ToggleMultipleTimes_DoesNotThrow()
    {
        var engine = _fixture.CreatePlainEngine();
        var service = new HoverPreviewService(engine);

        service.SetDebugEnabled(true);
        service.SetDebugEnabled(false);
        service.SetDebugEnabled(true);
    }

    // ── Constructor default debug disabled ───────────────────────────────────

    [Fact]
    public async Task Constructor_DebugEnabledTrue_LogsWithoutThrowing()
    {
        var service = _fixture.CreateHoverService(debugEnabled: true);

        var result = await service.BuildMarkdownAsync(
            filePath: "DebugMode.cs",
            sourceText: "int z = 0;",
            line: 0,
            character: 0,
            cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
    }

    // ── CancellationToken respected ──────────────────────────────────────────

    [Fact]
    public async Task BuildMarkdownAsync_PreCancelledToken_DoesNotHang()
    {
        var engine = _fixture.CreatePlainEngine();
        var service = new HoverPreviewService(engine);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Fast-fail paths (no expression/no assembly) return before reaching the
        // engine, so a pre-cancelled token should still produce a clean result.
        var result = await service.BuildMarkdownAsync(
            filePath: "Cancel.cs",
            sourceText: "var z = 0;",
            line: 0,
            character: 0,
            cancellationToken: cts.Token);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BuildStructuredAsync_FirstHoverDuringWarmup_ReturnsStartingStatus()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var sourcePath = CreateTempProjectWithOutput(tempDir.FullName, "HoverWarmupApp");
        var engine = _fixture.CreatePlainEngine();
        engine.InspectModelAsyncHandler = async (_, ct) =>
        {
            await Task.Delay(200, ct);
            return new ModelSnapshot { DbContextType = "FakeContext" };
        };

        var service = new HoverPreviewService(
            engine,
            new WarmupHandler(new DocumentManager(), engine));

        var result = await service.BuildStructuredAsync(
            filePath: sourcePath,
            sourceText: "var q = db.Orders.Where(o => o.Id > 0);",
            line: 0,
            character: 15,
            cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(QueryTranslationStatus.Starting, result.Status);
        Assert.Contains("warming up", result.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildStructuredAsync_AfterWarmupCompletes_DoesNotReturnStartingStatus()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var sourcePath = CreateTempProjectWithOutput(tempDir.FullName, "HoverWarmupApp");
        var engine = _fixture.CreatePlainEngine();
        var service = new HoverPreviewService(
            engine,
            new WarmupHandler(new DocumentManager(), engine));

        var first = await service.BuildStructuredAsync(
            filePath: sourcePath,
            sourceText: "var q = db.Orders.Where(o => o.Id > 0);",
            line: 0,
            character: 15,
            cancellationToken: CancellationToken.None);

        Assert.Equal(QueryTranslationStatus.Starting, first.Status);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        var second = await service.BuildStructuredAsync(
            filePath: sourcePath,
            sourceText: "var q = db.Orders.Where(o => o.Id > 0);",
            line: 0,
            character: 15,
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(QueryTranslationStatus.Starting, second.Status);
    }

    private static string CreateTempProjectWithOutput(string rootDir, string projectName)
    {
        var projectDir = Path.Combine(rootDir, projectName);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(
            Path.Combine(projectDir, $"{projectName}.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>");

        var sourcePath = Path.Combine(projectDir, "Query.cs");
        File.WriteAllText(sourcePath, "var q = db.Orders.Where(o => o.Id > 0);");

        var outputDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(Path.Combine(outputDir, $"{projectName}.dll"), [0]);

        return sourcePath;
    }
}
