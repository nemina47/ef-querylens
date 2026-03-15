using System.Reflection;
using EFQueryLens.Core;
using EFQueryLens.Core.Grpc;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Core.Tests.Lsp;

public class HoverHandlerDaemonEventTests
{
    [Theory]
    [MemberData(nameof(GetInvalidationEvents))]
    public void HandleDaemonEvent_WhenDaemonSignals_InvalidateAllHoverCaches(DaemonEvent daemonEvent)
    {
        var handler = CreateHandler();
        SeedCaches(handler);

        Assert.True(GetDictionaryCount(handler, "_hoverCache") > 0);
        Assert.True(GetDictionaryCount(handler, "_semanticHoverCache") > 0);
        Assert.True(GetDictionaryCount(handler, "_structuredHoverCache") > 0);
        Assert.True(GetDictionaryCount(handler, "_semanticStructuredHoverCache") > 0);

        handler.HandleDaemonEvent(daemonEvent);

        Assert.Equal(0, GetDictionaryCount(handler, "_hoverCache"));
        Assert.Equal(0, GetDictionaryCount(handler, "_semanticHoverCache"));
        Assert.Equal(0, GetDictionaryCount(handler, "_structuredHoverCache"));
        Assert.Equal(0, GetDictionaryCount(handler, "_semanticStructuredHoverCache"));
    }

    public static IEnumerable<object[]> GetInvalidationEvents()
    {
        yield return
        [
            new DaemonEvent
            {
                ConfigReloaded = new ConfigReloadedEvent(),
            },
        ];

        yield return
        [
            new DaemonEvent
            {
                StateChanged = new StateChangedEvent
                {
                    ContextName = "sample",
                    State = DaemonWarmState.Ready,
                },
            },
        ];

        yield return
        [
            new DaemonEvent
            {
                AssemblyChanged = new AssemblyChangedEvent
                {
                    ContextName = "sample",
                    AssemblyPath = "sample.dll",
                },
            },
        ];
    }

    private static HoverHandler CreateHandler()
    {
        var engine = new NoOpQueryLensEngine();
        return new HoverHandler(new DocumentManager(), new HoverPreviewService(engine));
    }

    private static void SeedCaches(HoverHandler handler)
    {
        var hover = new Hover();
        var structuredResult = new QueryLensStructuredHoverResult(
            Success: true,
            ErrorMessage: null,
            Statements: [],
            CommandCount: 0,
            SourceExpression: null,
            DbContextType: null,
            ProviderName: null,
            SourceFile: null,
            SourceLine: 0,
            Warnings: [],
            EnrichedSql: null,
            Mode: "direct",
            Status: QueryTranslationStatus.Ready,
            StatusMessage: null,
            AvgTranslationMs: 0);

        var semanticContextType = typeof(HoverHandler).GetNestedType(
            "SemanticHoverContext",
            BindingFlags.NonPublic);
        Assert.NotNull(semanticContextType);

        var semanticContext = Activator.CreateInstance(
            semanticContextType!,
            "project|db|query",
            4,
            8);
        Assert.NotNull(semanticContext);

        var cacheHoverMethod = typeof(HoverHandler).GetMethod(
            "CacheHover",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(cacheHoverMethod);

        var cacheStructuredMethod = typeof(HoverHandler).GetMethod(
            "CacheStructured",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(cacheStructuredMethod);

        cacheHoverMethod!.Invoke(handler, ["cursor-cache", hover, null]);
        cacheHoverMethod.Invoke(handler, ["semantic-cache", hover, semanticContext]);

        cacheStructuredMethod!.Invoke(handler, ["structured-cursor-cache", structuredResult, null]);
        cacheStructuredMethod.Invoke(handler, ["structured-semantic-cache", structuredResult, semanticContext]);
    }

    private static int GetDictionaryCount(HoverHandler handler, string fieldName)
    {
        var field = typeof(HoverHandler).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var instance = field!.GetValue(handler);
        Assert.NotNull(instance);

        var countProperty = instance!.GetType().GetProperty("Count");
        Assert.NotNull(countProperty);

        var countValue = countProperty!.GetValue(instance);
        Assert.NotNull(countValue);
        return (int)countValue!;
    }

    private sealed class NoOpQueryLensEngine : IQueryLensEngine
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<QueryTranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
            => Task.FromResult(new QueryTranslationResult());

        public Task<QueuedTranslationResult> TranslateQueuedAsync(TranslationRequest request, CancellationToken ct = default)
            => Task.FromResult(new QueuedTranslationResult
            {
                Status = QueryTranslationStatus.Ready,
                Result = new QueryTranslationResult(),
            });

        public Task<ExplainResult> ExplainAsync(ExplainRequest request, CancellationToken ct = default)
            => Task.FromResult(new ExplainResult());

        public Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ModelSnapshot
            {
                DbContextType = string.Empty,
            });
    }
}
