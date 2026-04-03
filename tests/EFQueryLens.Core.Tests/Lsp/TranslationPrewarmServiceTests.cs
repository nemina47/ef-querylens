using System.Reflection;
using EFQueryLens.Core;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Tests.Lsp.Fakes;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Hosting;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace EFQueryLens.Core.Tests.Lsp;

public sealed class TranslationPrewarmServiceTests : IDisposable
{
    private readonly string? _originalDebounceMs = Environment.GetEnvironmentVariable("QUERYLENS_CHANGE_PREWARM_DEBOUNCE_MS");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("QUERYLENS_CHANGE_PREWARM_DEBOUNCE_MS", _originalDebounceMs);
    }

    [Fact]
    public async Task WarmDocumentAsync_WhenAssemblyMissing_DoesNotInvokeCallback()
    {
        using var project = TempLspProject.Create(withAssembly: false);
        var callbacks = new List<string>();
        var service = CreatePrewarmService((filePath, _, _, _, _) => callbacks.Add(filePath));

        await InvokeWarmDocumentAsync(service, project.SourceFilePath, project.QuerySource);

        Assert.Empty(callbacks);
    }

    [Fact]
    public async Task WarmDocumentAsync_WhenNoLinqChains_DoesNotInvokeCallback()
    {
        using var project = TempLspProject.Create(withAssembly: true);
        var callbacks = new List<string>();
        var service = CreatePrewarmService((filePath, _, _, _, _) => callbacks.Add(filePath));

        await InvokeWarmDocumentAsync(service, project.SourceFilePath, "var x = 1;");

        Assert.Empty(callbacks);
    }

    [Fact]
    public async Task WarmDocumentAsync_WithDetectedChain_InvokesCallback()
    {
        using var project = TempLspProject.Create(withAssembly: true);
        var combinedResults = new List<CombinedHoverResult>();
        var service = CreatePrewarmService((_, _, _, _, combined) => combinedResults.Add(combined));

        await InvokeWarmDocumentAsync(service, project.SourceFilePath, project.QuerySource);

        Assert.Single(combinedResults);
        Assert.True(combinedResults[0].Markdown.Success);
        Assert.Equal(QueryTranslationStatus.Ready, combinedResults[0].Markdown.Status);
    }

    [Fact]
    public async Task DebounceWarmDocument_SupersedesPendingWarm_ForSameFile()
    {
        Environment.SetEnvironmentVariable("QUERYLENS_CHANGE_PREWARM_DEBOUNCE_MS", "25");

        using var project = TempLspProject.Create(withAssembly: true);
        var callbackSources = new List<string>();
        var service = CreatePrewarmService((_, sourceText, _, _, _) =>
        {
            lock (callbackSources)
            {
                callbackSources.Add(sourceText);
            }
        });

        service.DebounceWarmDocument(project.SourceFilePath, project.QuerySource.Replace("Id > 0", "Id > 1", StringComparison.Ordinal));
        service.DebounceWarmDocument(project.SourceFilePath, project.QuerySource.Replace("Id > 0", "Id > 2", StringComparison.Ordinal));

        var completed = await WaitForAsync(() =>
        {
            lock (callbackSources)
            {
                return callbackSources.Count == 1;
            }
        }, timeoutMs: 3000);

        Assert.True(completed);
        Assert.Single(callbackSources);
        Assert.Contains("Id > 2", callbackSources[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StorePrewarmedEntry_ReadyResult_IsReturnedByStructuredHover()
    {
        using var project = TempLspProject.Create(withAssembly: true);
        var documentManager = new DocumentManager();
        var documentUri = new Uri(project.SourceFilePath);
        documentManager.UpdateDocument(documentUri.ToString(), project.QuerySource);

        var handler = new HoverHandler(documentManager, new HoverPreviewService(new TestControllableEngine()));
        var combined = new CombinedHoverResult(
            new HoverPreviewComputationResult(
                Success: true,
                Output: "ready",
                Status: QueryTranslationStatus.Ready),
            new QueryLensStructuredHoverResult(
                Success: true,
                ErrorMessage: null,
                Statements: [new QueryLensSqlStatement("SELECT 1", null)],
                CommandCount: 1,
                SourceExpression: "context.Orders.Where(o => o.Id > 0)",
                ExecutedExpression: null,
                DbContextType: "AppDbContext",
                ProviderName: "Provider",
                SourceFile: project.SourceFilePath,
                SourceLine: 11,
                Warnings: [],
                EnrichedSql: "SELECT 1",
                Mode: "direct",
                Status: QueryTranslationStatus.Ready));

        handler.StorePrewarmedEntry(project.SourceFilePath, project.QuerySource, 11, 24, combined);

        var structured = await handler.HandleStructuredAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = documentUri },
                Position = new Position(11, 24),
            },
            CancellationToken.None);

        Assert.NotNull(structured);
        Assert.True(structured.Success);
        Assert.Equal(QueryTranslationStatus.Ready, structured.Status);
        Assert.Equal("SELECT 1", structured.EnrichedSql);
    }

    [Fact]
    public void StorePrewarmedEntry_InQueueResult_DoesNotPopulateCache()
    {
        using var project = TempLspProject.Create(withAssembly: true);
        var handler = new HoverHandler(new DocumentManager(), new HoverPreviewService(new TestControllableEngine()));
        var combined = new CombinedHoverResult(
            new HoverPreviewComputationResult(
                Success: false,
                Output: "wait",
                Status: QueryTranslationStatus.InQueue),
            new QueryLensStructuredHoverResult(
                Success: false,
                ErrorMessage: null,
                Statements: [],
                CommandCount: 0,
                SourceExpression: null,
                ExecutedExpression: null,
                DbContextType: null,
                ProviderName: null,
                SourceFile: null,
                SourceLine: 0,
                Warnings: [],
                EnrichedSql: null,
                Mode: null,
                Status: QueryTranslationStatus.InQueue,
                StatusMessage: "Computing"));

        handler.StorePrewarmedEntry(project.SourceFilePath, project.QuerySource, 11, 24, combined);

        Assert.Equal(0, GetDictionaryCount(handler, "_hoverCache"));
        Assert.Equal(0, GetDictionaryCount(handler, "_semanticHoverCache"));
    }

    private static TranslationPrewarmService CreatePrewarmService(Action<string, string, int, int, CombinedHoverResult> onPrewarmed)
    {
        return new TranslationPrewarmService(
            new HoverPreviewService(new TestControllableEngine()),
            onPrewarmed);
    }

    private static async Task InvokeWarmDocumentAsync(TranslationPrewarmService service, string filePath, string sourceText)
    {
        var method = typeof(TranslationPrewarmService).GetMethod(
            "WarmDocumentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WarmDocumentAsync not found.");

        var task = (Task)method.Invoke(service, [filePath, sourceText, CancellationToken.None])!;
        await task;
    }

    private static int GetDictionaryCount(HoverHandler handler, string fieldName)
    {
        var field = typeof(HoverHandler).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {fieldName} not found.");
        var dictionary = field.GetValue(handler)
            ?? throw new InvalidOperationException($"Field {fieldName} is null.");
        return (int)(dictionary.GetType().GetProperty("Count")?.GetValue(dictionary)
            ?? throw new InvalidOperationException($"Field {fieldName} count unavailable."));
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    internal sealed class TempLspProject : IDisposable
    {
        public string RootPath { get; }
        public string SourceFilePath { get; }
        public string QuerySource { get; }

        private TempLspProject(string rootPath, string sourceFilePath, string querySource)
        {
            RootPath = rootPath;
            SourceFilePath = sourceFilePath;
            QuerySource = querySource;
        }

        public static TempLspProject Create(bool withAssembly)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), $"ql-prewarm-{Guid.NewGuid():N}");
            var projectDir = Path.Combine(rootPath, "App");
            var outputDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
            Directory.CreateDirectory(outputDir);

            var querySource = """
using System.Linq;
public sealed class AppDbContext
{
    public IQueryable<Order> Orders => Array.Empty<Order>().AsQueryable();
}
public sealed class Order { public int Id { get; set; } }
public sealed class C
{
    private readonly AppDbContext context = new();
    public void M()
    {
        var q = context.Orders.Where(o => o.Id > 0);
    }
}
""";

            File.WriteAllText(
                Path.Combine(projectDir, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>App</AssemblyName></PropertyGroup></Project>");
            var sourceFilePath = Path.Combine(projectDir, "QueryFile.cs");
            File.WriteAllText(sourceFilePath, querySource);

            if (withAssembly)
            {
                File.WriteAllBytes(Path.Combine(outputDir, "App.dll"), [0x4D, 0x5A]);
            }

            return new TempLspProject(rootPath, sourceFilePath, querySource);
        }

        public static TempLspProject CreateWithHelperCallsite()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), $"ql-prewarm-helper-{Guid.NewGuid():N}");
            var projectDir = Path.Combine(rootPath, "App");
            var outputDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
            Directory.CreateDirectory(outputDir);

            var querySource = """
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
public sealed class AppDbContext { public IQueryable<Order> Orders => Array.Empty<Order>().AsQueryable(); }
public sealed class Order { public int Id { get; set; } public bool IsDeleted { get; set; } }
public sealed class Svc
{
    private readonly AppDbContext db = new();
    public Task<int?> M(Guid id, CancellationToken ct)
    {
        return GetByIdAsync(id, o => o.Id, ct);
    }
    private Task<TResult?> GetByIdAsync<TResult>(Guid id, Expression<Func<Order, TResult>> expression, CancellationToken ct)
    {
        return db.Orders
            .Where(o => !o.IsDeleted)
            .Where(o => o.Id == id)
            .Select(expression)
            .SingleOrDefaultAsync(ct);
    }
}
""";

            File.WriteAllText(
                Path.Combine(projectDir, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>App</AssemblyName></PropertyGroup></Project>");
            var sourceFilePath = Path.Combine(projectDir, "QueryFile.cs");
            File.WriteAllText(sourceFilePath, querySource);
            File.WriteAllBytes(Path.Combine(outputDir, "App.dll"), [0x4D, 0x5A]);

            return new TempLspProject(rootPath, sourceFilePath, querySource);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch
            {
            }
        }
    }
}

public class LspHoverAndCommandFlowTests
{
    [Fact]
    public async Task HandleAsync_ReturnsNull_WhenDocumentMissing()
    {
        var handler = new HoverHandler(new DocumentManager(), new HoverPreviewService(new TestControllableEngine()));

        var hover = await handler.HandleAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = new Uri("file:///c:/missing/file.cs") },
                Position = new Position(0, 0),
            },
            CancellationToken.None);

        Assert.Null(hover);
    }

    [Fact]
    public async Task HandleAsync_WithCacheDisabled_ComputesImmediateHover()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var docs = new DocumentManager();
        var uri = new Uri(project.SourceFilePath);
        docs.UpdateDocument(uri.ToString(), project.QuerySource);
        var handler = new HoverHandler(docs, new HoverPreviewService(new TestControllableEngine()));
        SetField(handler, "_hoverCacheTtlMs", 0);

        var hover = await handler.HandleAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(11, 24),
            },
            CancellationToken.None);

        Assert.NotNull(hover);
        Assert.Equal(0, GetDictionaryCount(handler, "_hoverCache"));
    }

    [Fact]
    public async Task HandleAsync_WithCacheEnabled_QueuesInPrimaryCache()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var docs = new DocumentManager();
        var uri = new Uri(project.SourceFilePath);
        docs.UpdateDocument(uri.ToString(), project.QuerySource);
        var handler = new HoverHandler(docs, new HoverPreviewService(new TestControllableEngine()));

        var hover = await handler.HandleAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(11, 24),
            },
            CancellationToken.None);

        Assert.NotNull(hover);
        Assert.True(GetDictionaryCount(handler, "_hoverCache") >= 1);
    }

    [Fact]
    public async Task HoverAsync_WithCompletedHoverTask_ReturnsCachedResult_WithoutProgress()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var docs = new DocumentManager();
        var uri = new Uri(project.SourceFilePath);
        docs.UpdateDocument(uri.ToString(), project.QuerySource);

        var hoverHandler = new HoverHandler(docs, new HoverPreviewService(new TestControllableEngine()));
        hoverHandler.StorePrewarmedEntry(project.SourceFilePath, project.QuerySource, 11, 24, BuildReadyCombined(project.SourceFilePath));

        var handler = CreateLanguageServerHandler(docs, hoverHandler, new TestControllableEngine());
        SetField(handler, "_hoverProgressEnabled", true);
        handler.JsonRpc = CreateDummyJsonRpc();

        var hover = await handler.HoverAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(11, 24),
            },
            CancellationToken.None);

        Assert.NotNull(hover);
    }

    [Fact]
    public async Task RecalculatePreviewAsync_InvalidatesAndReturnsStructuredHover()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var docs = new DocumentManager();
        var uri = new Uri(project.SourceFilePath);
        docs.UpdateDocument(uri.ToString(), project.QuerySource);

        var engine = new TestControllableEngine();
        var hoverHandler = new HoverHandler(docs, new HoverPreviewService(engine));
        SetField(hoverHandler, "_hoverCacheTtlMs", 0);
        var handler = CreateLanguageServerHandler(docs, hoverHandler, engine);

        var result = await handler.RecalculatePreviewAsync(
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(11, 24),
            },
            CancellationToken.None);

        Assert.True(result["success"]!.Value<bool>());
        Assert.NotNull(result["hover"]);
        Assert.Equal(1, engine.InvalidateCalls);
    }

    [Fact]
    public async Task TryStartHoverProgressAsync_WithUnavailableRpc_ReturnsFalse()
    {
        var docs = new DocumentManager();
        var handler = CreateLanguageServerHandler(docs, new HoverHandler(docs, new HoverPreviewService(new TestControllableEngine())), new TestControllableEngine());
        handler.JsonRpc = CreateDummyJsonRpc();

        var started = Assert.IsType<bool>(await InvokePrivateAsync(handler, "TryStartHoverProgressAsync", ["token-1"]));

        Assert.False(started);
    }

    [Fact]
    public async Task TryEndHoverProgressAsync_WithUnavailableRpc_DoesNotThrow()
    {
        var docs = new DocumentManager();
        var handler = CreateLanguageServerHandler(docs, new HoverHandler(docs, new HoverPreviewService(new TestControllableEngine())), new TestControllableEngine());
        handler.JsonRpc = CreateDummyJsonRpc();

        await InvokePrivateAsync(handler, "TryEndHoverProgressAsync", ["token-1", "done"]);
    }

    private static CombinedHoverResult BuildReadyCombined(string sourceFilePath) =>
        new(
            new HoverPreviewComputationResult(true, "SELECT 1", QueryTranslationStatus.Ready),
            new QueryLensStructuredHoverResult(
                Success: true,
                ErrorMessage: null,
                Statements: [new QueryLensSqlStatement("SELECT 1", null)],
                CommandCount: 1,
                SourceExpression: "context.Orders.Where(o => o.Id > 0)",
                ExecutedExpression: null,
                DbContextType: "AppDbContext",
                ProviderName: "Provider",
                SourceFile: sourceFilePath,
                SourceLine: 11,
                Warnings: [],
                EnrichedSql: "SELECT 1",
                Mode: "direct",
                Status: QueryTranslationStatus.Ready));

    private static LanguageServerHandler CreateLanguageServerHandler(DocumentManager docs, HoverHandler hoverHandler, TestControllableEngine engine)
    {
        return new LanguageServerHandler(
            hoverHandler,
            new WarmupHandler(docs, engine),
            new DaemonControlHandler(engine),
            new TextDocumentSyncHandler(docs),
            new GenerateFactoryHandler(engine),
            debugEnabled: false);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {fieldName} not found.");
        field.SetValue(target, value);
    }

    private static int GetDictionaryCount(HoverHandler handler, string fieldName)
    {
        var field = typeof(HoverHandler).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {fieldName} not found.");
        var dictionary = field.GetValue(handler)
            ?? throw new InvalidOperationException($"Field {fieldName} is null.");
        return (int)(dictionary.GetType().GetProperty("Count")?.GetValue(dictionary)
            ?? throw new InvalidOperationException($"Field {fieldName} count unavailable."));
    }

    private static async Task<object?> InvokePrivateAsync(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");

        var result = method.Invoke(target, args);
        if (result is Task task)
        {
            await task;
            var resultProperty = task.GetType().GetProperty("Result");
            return resultProperty?.GetValue(task);
        }

        return result;
    }

    private static JsonRpc CreateDummyJsonRpc()
    {
        var formatter = new JsonMessageFormatter();
        var messageHandler = new HeaderDelimitedMessageHandler(new MemoryStream(), new MemoryStream(), formatter);
        return new JsonRpc(messageHandler, new object());
    }
}

public class LspWarmupAndPreviewWrapperTests
{
    [Fact]
    public async Task BuildStructuredAsync_SendsAuthoritativeRewriteContractToEngine()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var engine = new CapturingTranslateEngine();
        var service = new HoverPreviewService(engine);

        var result = await service.BuildStructuredAsync(
            project.SourceFilePath,
            project.QuerySource,
            11,
            24,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(engine.LastRequest);
        Assert.False(string.IsNullOrWhiteSpace(engine.LastRequest.RewrittenExpression));
        Assert.False(string.IsNullOrWhiteSpace(engine.LastRequest.OriginalExpression));
        Assert.Contains("lsp-extraction", engine.LastRequest.RewriteFlags);
    }

    [Fact]
    public async Task BuildMarkdownAsync_WithValidQuery_ReturnsReadyMarkdown()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var service = new HoverPreviewService(new TestControllableEngine());

        var result = await service.BuildMarkdownAsync(project.SourceFilePath, project.QuerySource, 11, 24, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(QueryTranslationStatus.Ready, result.Status);
        Assert.Contains("EF QueryLens", result.Output, StringComparison.Ordinal);
        Assert.Contains("Copy SQL", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildStructuredAsync_WithValidQuery_ReturnsStructuredResult()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var service = new HoverPreviewService(new TestControllableEngine());

        var result = await service.BuildStructuredAsync(project.SourceFilePath, project.QuerySource, 11, 24, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(QueryTranslationStatus.Ready, result.Status);
        Assert.Single(result.Statements);
        Assert.Contains("SELECT", result.Statements[0].Sql, StringComparison.Ordinal);
        Assert.Contains("1", result.Statements[0].Sql, StringComparison.Ordinal);
        Assert.Contains("EF QueryLens", result.EnrichedSql, StringComparison.Ordinal);
        Assert.Contains("SELECT", result.EnrichedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildStructuredAsync_HelperExtraction_FromNonQueryableCallsite_DoesNotSemanticGateBlock()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.CreateWithHelperCallsite();
        var service = new HoverPreviewService(new TestControllableEngine());
        var (line, character) = FindPosition(project.QuerySource, "GetByIdAsync(id, o => o.Id, ct)");

        var result = await service.BuildStructuredAsync(
            project.SourceFilePath,
            project.QuerySource,
            line,
            character,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(QueryTranslationStatus.Ready, result.Status);
        Assert.Single(result.Statements);
    }

    [Fact]
    public async Task HandleAsync_WithSuccessfulInspectModel_ReturnsReadyAndCachesResult()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var docs = new DocumentManager();
        var uri = new Uri(project.SourceFilePath);
        docs.UpdateDocument(uri.ToString(), project.QuerySource);
        var engine = new InspectModelEngine();
        var handler = new WarmupHandler(docs, engine);

        var first = await handler.HandleAsync(CreatePositionRequest(uri), CancellationToken.None);
        var second = await handler.HandleAsync(CreatePositionRequest(uri), CancellationToken.None);

        Assert.True(first.Success);
        Assert.False(first.Cached);
        Assert.Equal("ready", first.Message);
        Assert.True(second.Success);
        Assert.True(second.Cached);
        Assert.Equal(1, engine.InspectCalls);
    }

    [Fact]
    public async Task HandleAsync_WhenInspectModelHasMultiDbContextAmbiguity_ReturnsSkipped()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var docs = new DocumentManager();
        var uri = new Uri(project.SourceFilePath);
        docs.UpdateDocument(uri.ToString(), project.QuerySource);
        var engine = new InspectModelEngine
        {
            InspectException = new InvalidOperationException(
                "outer",
                new DbContextDiscoveryException(DbContextDiscoveryFailureKind.MultipleDbContextsFound, "many")),
        };
        var handler = new WarmupHandler(docs, engine);

        var response = await handler.HandleAsync(CreatePositionRequest(uri), CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal("skipped-multi-dbcontext", response.Message);
    }

    [Fact]
    public async Task HandleAsync_WhenInspectModelFails_ReturnsFailureTypeName()
    {
        using var project = TranslationPrewarmServiceTests.TempLspProject.Create(withAssembly: true);
        var docs = new DocumentManager();
        var uri = new Uri(project.SourceFilePath);
        docs.UpdateDocument(uri.ToString(), project.QuerySource);
        var engine = new InspectModelEngine
        {
            InspectException = new InvalidOperationException("boom"),
        };
        var handler = new WarmupHandler(docs, engine);

        var response = await handler.HandleAsync(CreatePositionRequest(uri), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("InvalidOperationException", response.Message);
    }

    private static TextDocumentPositionParams CreatePositionRequest(Uri uri) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position = new Position(11, 24),
        };

    private sealed class InspectModelEngine : IQueryLensEngine
    {
        public Exception? InspectException { get; set; }
        public int InspectCalls { get; private set; }

        public Task<QueryTranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
            => Task.FromResult(new QueryTranslationResult
            {
                Success = true,
                Sql = "SELECT 1",
                Metadata = new TranslationMetadata
                {
                    DbContextType = "AppDbContext",
                    EfCoreVersion = "9.0.0",
                    ProviderName = "Provider",
                    TranslationTime = TimeSpan.FromMilliseconds(1),
                },
            });

        public Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
        {
            InspectCalls++;
            if (InspectException is not null)
            {
                return Task.FromException<ModelSnapshot>(InspectException);
            }

            return Task.FromResult(new ModelSnapshot { DbContextType = "AppDbContext" });
        }

        public Task<FactoryGenerationResult> GenerateFactoryAsync(FactoryGenerationRequest request, CancellationToken ct = default)
            => Task.FromResult(new FactoryGenerationResult { Content = "// generated", SuggestedFileName = "Factory.cs", DbContextTypeFullName = "AppDbContext" });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingTranslateEngine : IQueryLensEngine
    {
        public TranslationRequest? LastRequest { get; private set; }

        public Task<QueryTranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new QueryTranslationResult
            {
                Success = true,
                Sql = "SELECT 1",
                Metadata = new TranslationMetadata
                {
                    DbContextType = "AppDbContext",
                    EfCoreVersion = "9.0.0",
                    ProviderName = "Provider",
                    TranslationTime = TimeSpan.FromMilliseconds(1),
                },
            });
        }

        public Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ModelSnapshot { DbContextType = "AppDbContext" });

        public Task<FactoryGenerationResult> GenerateFactoryAsync(FactoryGenerationRequest request, CancellationToken ct = default)
            => Task.FromResult(new FactoryGenerationResult { Content = "// generated", SuggestedFileName = "Factory.cs", DbContextTypeFullName = "AppDbContext" });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (int line, int character) FindPosition(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Marker '{marker}' not found.");

        var line = 0;
        var character = 0;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return (line, character);
    }
}
