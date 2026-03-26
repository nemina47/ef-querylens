using System.Reflection;
using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using ModelSnapshot = EFQueryLens.Core.Contracts.ModelSnapshot;

namespace EFQueryLens.Core.Tests.Lsp;

public class HoverHandlerDaemonEventTests
{
    [Fact]
    public void OnAssemblyChanged_InvalidatesAllHoverCaches()
    {
        var handler = CreateHandler();
        SeedCaches(handler);

        Assert.True(GetDictionaryCount(handler, "_hoverCache") > 0);
        Assert.True(GetDictionaryCount(handler, "_semanticHoverCache") > 0);

        handler.OnAssemblyChanged();

        Assert.Equal(0, GetDictionaryCount(handler, "_hoverCache"));
        Assert.Equal(0, GetDictionaryCount(handler, "_semanticHoverCache"));
    }

    [Fact]
    public void InvalidateForManualRecalculate_InvalidatesAllHoverCaches()
    {
        var handler = CreateHandler();
        SeedCaches(handler);

        handler.InvalidateForManualRecalculate();

        Assert.Equal(0, GetDictionaryCount(handler, "_hoverCache"));
        Assert.Equal(0, GetDictionaryCount(handler, "_semanticHoverCache"));
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
            ExecutedExpression: null,
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

        var computedEntryType = typeof(HoverHandler).GetNestedType(
            "ComputedEntry",
            BindingFlags.NonPublic);
        Assert.NotNull(computedEntryType);

        var computedEntry = Activator.CreateInstance(
            computedEntryType!,
            hover,
            structuredResult,
            QueryTranslationStatus.Ready);
        Assert.NotNull(computedEntry);

        var cacheEntryMethod = typeof(HoverHandler).GetMethod(
            "CacheEntry",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(cacheEntryMethod);

        // Populate position cache (semanticContext = null → only _hoverCache)
        cacheEntryMethod!.Invoke(handler, ["cursor-cache", computedEntry, null]);
        // Populate position + semantic cache
        cacheEntryMethod.Invoke(handler, ["semantic-cache", computedEntry, semanticContext]);
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

        public Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ModelSnapshot
            {
                DbContextType = string.Empty,
            });
    }
}
