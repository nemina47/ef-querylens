using EFQueryLens.Integration.Tests.Lsp.Fakes;
using EFQueryLens.Integration.Tests.Lsp.Fixtures;
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
}
