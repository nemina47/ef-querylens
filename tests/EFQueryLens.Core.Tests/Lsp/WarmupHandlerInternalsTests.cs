using System.Reflection;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Tests.Lsp.Fakes;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace EFQueryLens.Core.Tests.Lsp;

public class WarmupHandlerInternalsTests
{
    [Fact]
    public async Task HandleAsync_ReturnsEmptySource_WhenNoDocumentAndNoFile()
    {
        var handler = new WarmupHandler(new DocumentManager(), new TestControllableEngine());

        var response = await handler.HandleAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = new Uri("file:///c:/does-not-exist/sample.cs") },
                Position = new Position(0, 0),
            },
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal("empty-source", response.Message);
    }

    [Fact]
    public async Task HandleAsync_ReturnsNoLinqChain_WhenSourceHasNoQuery()
    {
        var docs = new DocumentManager();
        var uri = new Uri("file:///c:/repo/noquery.cs");
        docs.UpdateDocument(uri.ToString(), "var x = 1;");
        var handler = new WarmupHandler(docs, new TestControllableEngine());

        var response = await handler.HandleAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(0, 0),
            },
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal("no-linq-chain", response.Message);
    }

    [Fact]
    public async Task HandleAsync_ReturnsAssemblyNotFound_WhenResolverFails()
    {
        var docs = new DocumentManager();
        var tempDir = Directory.CreateTempSubdirectory();
        var sourcePath = Path.Combine(tempDir.FullName, "query.cs");
        await File.WriteAllTextAsync(sourcePath, "var q = db.Orders.Where(o => o.Id > 0);");
        var uri = new Uri(sourcePath);
        docs.UpdateDocument(uri.ToString(), "var q = db.Orders.Where(o => o.Id > 0);");
        var handler = new WarmupHandler(docs, new TestControllableEngine());

        var response = await handler.HandleAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(0, 15),
            },
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal("assembly-not-found", response.Message);
    }

    [Fact]
    public void CacheWarmup_ThenTryGetCachedWarmup_RoundTripsUntilExpiration()
    {
        var handler = new WarmupHandler(new DocumentManager(), new TestControllableEngine());

        SetField(handler, "_successTtlMs", 1000);
        InvokePrivate(handler, "CacheWarmup", ["a.dll", true, "ready"]);

        var outBox = new object?[] { "a.dll", null };
        var hit = (bool)InvokePrivate(handler, "TryGetCachedWarmup", outBox)!;

        Assert.True(hit);
        Assert.NotNull(outBox[1]);
    }

    [Fact]
    public void CacheWarmup_WithZeroTtl_RemovesEntry()
    {
        var handler = new WarmupHandler(new DocumentManager(), new TestControllableEngine());

        SetField(handler, "_successTtlMs", 0);
        InvokePrivate(handler, "CacheWarmup", ["a.dll", true, "ready"]);

        var outBox = new object?[] { "a.dll", null };
        var hit = (bool)InvokePrivate(handler, "TryGetCachedWarmup", outBox)!;

        Assert.False(hit);
    }

    [Fact]
    public void ApplyClientConfiguration_UpdatesFields()
    {
        var handler = new WarmupHandler(new DocumentManager(), new TestControllableEngine());

        handler.ApplyClientConfiguration(new LspClientConfiguration(
            DebugEnabled: true,
            WarmupSuccessTtlMs: 1234,
            WarmupFailureCooldownMs: 4321));

        Assert.Equal(1234, GetField<int>(handler, "_successTtlMs"));
        Assert.Equal(4321, GetField<int>(handler, "_failureCooldownMs"));
        Assert.True(GetField<bool>(handler, "_debugEnabled"));
    }

    [Fact]
    public void IsMultipleDbContextAmbiguity_ReturnsTrue_ForNestedDiscoveryException()
    {
        var ex = new InvalidOperationException(
            "outer",
            new DbContextDiscoveryException(DbContextDiscoveryFailureKind.MultipleDbContextsFound, "many contexts"));

        var result = (bool)InvokePrivateStatic(typeof(WarmupHandler), "IsMultipleDbContextAmbiguity", [ex])!;

        Assert.True(result);
    }

    [Fact]
    public void IsMultipleDbContextAmbiguity_ReturnsFalse_WhenFailureKindDiffers()
    {
        var ex = new DbContextDiscoveryException(DbContextDiscoveryFailureKind.NoDbContextFound, "none");

        var result = (bool)InvokePrivateStatic(typeof(WarmupHandler), "IsMultipleDbContextAmbiguity", [ex])!;

        Assert.False(result);
    }

    [Fact]
    public void TryResolveDbContextTypeName_ReturnsType_FromLocalDeclaration()
    {
        const string source = """
using System.Linq;
public class C
{
    private readonly AppDbContext context = new();
    public void M()
    {
        var q = context.Orders.Where(o => o.Id > 0);
    }
}
""";

        var result = (string?)InvokePrivateStatic(
            typeof(WarmupHandler),
            "TryResolveDbContextTypeName",
            [source, 6, 20]);

        Assert.Equal("AppDbContext", result);
    }

    [Fact]
    public async Task GetSourceTextAsync_ReadsFromFile_WhenDocumentCacheMisses()
    {
        var docs = new DocumentManager();
        var handler = new WarmupHandler(docs, new TestControllableEngine());
        var tempDir = Directory.CreateTempSubdirectory();
        var sourcePath = Path.Combine(tempDir.FullName, "source.cs");
        await File.WriteAllTextAsync(sourcePath, "var q = db.Orders.Where(o => o.Id > 0);", TestContext.Current.CancellationToken);
        var uri = new Uri(sourcePath).ToString();

        var task = (Task<string?>)InvokePrivate(handler, "GetSourceTextAsync", [uri, sourcePath, TestContext.Current.CancellationToken])!;
        var text = await task;

        Assert.Contains("Orders", text);
    }

    [Fact]
    public async Task GetSourceTextAsync_ReturnsNull_WhenFileMissingAndNoDocument()
    {
        var handler = new WarmupHandler(new DocumentManager(), new TestControllableEngine());

        var task = (Task<string?>)InvokePrivate(
            handler,
            "GetSourceTextAsync",
            ["file:///missing.cs", "c:/definitely-missing/file.cs", TestContext.Current.CancellationToken])!;
        var text = await task;

        Assert.Null(text);
    }

    [Fact]
    public void EnsureWarmupStartedForPreview_FirstCall_StartsWarmupAndDefersPreview()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var sourcePath = CreateTempProjectWithOutput(tempDir.FullName, "App");
        var engine = new TestControllableEngine { InspectModelDelay = TimeSpan.FromMilliseconds(200) };
        var handler = new WarmupHandler(new DocumentManager(), engine);

        var state = handler.EnsureWarmupStartedForPreview(
            sourcePath,
            "var q = db.Orders.Where(o => o.Id > 0);",
            0,
            15);

        Assert.True(state.ShouldDeferPreview);
        Assert.Equal("warmup-started", state.Reason);
        Assert.Contains("warming up", state.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureWarmupStartedForPreview_AfterSuccessfulWarmup_DoesNotDeferPreview()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var sourcePath = CreateTempProjectWithOutput(tempDir.FullName, "App");
        var engine = new TestControllableEngine();
        var handler = new WarmupHandler(new DocumentManager(), engine);

        var initial = handler.EnsureWarmupStartedForPreview(
            sourcePath,
            "var q = db.Orders.Where(o => o.Id > 0);",
            0,
            15);

        Assert.True(initial.ShouldDeferPreview);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        var after = handler.EnsureWarmupStartedForPreview(
            sourcePath,
            "var q = db.Orders.Where(o => o.Id > 0);",
            0,
            15);

        Assert.False(after.ShouldDeferPreview);
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

    private static object? InvokePrivate(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        return method.Invoke(target, args);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {fieldName} not found.");
        field.SetValue(target, value);
    }

    private static T GetField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {fieldName} not found.");
        return (T)field.GetValue(target)!;
    }

    private static object? InvokePrivateStatic(Type type, string methodName, object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        return method.Invoke(null, args);
    }
}
