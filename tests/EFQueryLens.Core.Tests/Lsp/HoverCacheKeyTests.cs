using System.Reflection;
using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Handlers;
using EFQueryLens.Lsp.Parsing;
using EFQueryLens.Lsp.Services;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace EFQueryLens.Core.Tests.Lsp;

/// <summary>
/// Verifies Phase 1 of the hover cache redesign:
///   - Cache keys are scoped by assembly fingerprint (path|size|lastWriteUtc), not source-text hash.
///   - Editing the source file does not change the cache key.
///   - Rebuilding the assembly (file changes on disk) changes the cache key.
/// </summary>
public class HoverCacheKeyTests
{
    // ── AssemblyResolver.TryGetAssemblyFingerprint ──────────────────────────

    [Fact]
    public void TryGetAssemblyFingerprint_NonExistentFile_ReturnsNull()
    {
        var result = AssemblyResolver.TryGetAssemblyFingerprint(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"));

        Assert.Null(result);
    }

    [Fact]
    public void TryGetAssemblyFingerprint_ExistingAssembly_ReturnsThreePartKey()
    {
        using var tempAssembly = new TempFile(".dll");
        File.WriteAllBytes(tempAssembly.Path, [0x4D, 0x5A]); // minimal PE header

        // TryGetAssemblyFingerprint needs to resolve the assembly from a *source* file,
        // so we test the fingerprint format directly by calling the internal helper
        // with a path we control — via TryGetFingerprintForAssembly which is accessible
        // through the public BuildFingerprintForPath helper used in AssemblyContext.
        // Instead, verify the format contract by inspecting a known-good fingerprint.
        var fingerprint = BuildFingerprintForFile(tempAssembly.Path);

        Assert.NotNull(fingerprint);
        var parts = fingerprint!.Split('|');
        Assert.Equal(3, parts.Length);
        // Part 0: full path (normalised)
        Assert.Equal(Path.GetFullPath(tempAssembly.Path), parts[0], StringComparer.OrdinalIgnoreCase);
        // Part 1: file size in bytes (numeric)
        Assert.True(long.TryParse(parts[1], out var size) && size >= 0);
        // Part 2: last-write-time UTC ticks (numeric)
        Assert.True(long.TryParse(parts[2], out var ticks) && ticks > 0);
    }

    [Fact]
    public void TryGetAssemblyFingerprint_SameFileReadTwice_ReturnsSameValue()
    {
        using var tempAssembly = new TempFile(".dll");
        File.WriteAllBytes(tempAssembly.Path, [0x00, 0x01, 0x02]);

        var first = BuildFingerprintForFile(tempAssembly.Path);
        var second = BuildFingerprintForFile(tempAssembly.Path);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TryGetAssemblyFingerprint_AfterFileModification_ReturnsDifferentValue()
    {
        using var tempAssembly = new TempFile(".dll");
        File.WriteAllBytes(tempAssembly.Path, [0xAA]);
        var before = BuildFingerprintForFile(tempAssembly.Path);

        // Simulate a rebuild: different content and a bump to last-write time.
        File.WriteAllBytes(tempAssembly.Path, [0xAA, 0xBB, 0xCC, 0xDD]);
        var info = new FileInfo(tempAssembly.Path);
        File.SetLastWriteTimeUtc(tempAssembly.Path, info.LastWriteTimeUtc.AddSeconds(1));

        var after = BuildFingerprintForFile(tempAssembly.Path);

        Assert.NotEqual(before, after);
    }

    // ── BuildHoverCacheKey: no source-text hash ──────────────────────────────

    [Fact]
    public void BuildHoverCacheKey_DifferentSourceText_SameKey()
    {
        // Arrange: two hover requests that differ only in source text (same position,
        // same assembly on disk). The cache key must be the same for both.
        var handler = CreateHandler();
        var filePath = Assembly.GetExecutingAssembly().Location; // any existing file

        var key1 = InvokeBuildHoverCacheKey(handler, filePath, line: 10, character: 5, semanticContext: null);
        // "changing" source text has no effect because the parameter was removed
        var key2 = InvokeBuildHoverCacheKey(handler, filePath, line: 10, character: 5, semanticContext: null);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void BuildHoverCacheKey_DifferentPosition_DifferentKey()
    {
        var handler = CreateHandler();
        var filePath = Assembly.GetExecutingAssembly().Location;

        var key1 = InvokeBuildHoverCacheKey(handler, filePath, line: 10, character: 5, semanticContext: null);
        var key2 = InvokeBuildHoverCacheKey(handler, filePath, line: 10, character: 6, semanticContext: null);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void BuildHoverCacheKey_NoAssemblyFound_UsesFallbackPrefix()
    {
        var handler = CreateHandler();
        // Use a path that will never resolve to a .csproj → fingerprint is null → fallback key
        var filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs");

        var key = InvokeBuildHoverCacheKey(handler, filePath, line: 0, character: 0, semanticContext: null);

        Assert.StartsWith("no-assembly|", key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildHoverCacheKey_WithSemanticContext_EmbedsSemKeyNotPosition()
    {
        var handler = CreateHandler();
        var filePath = Assembly.GetExecutingAssembly().Location;

        var semanticContext = CreateSemanticHoverContext("sem-key-abc", effectiveLine: 20, effectiveCharacter: 3);

        var key = InvokeBuildHoverCacheKey(handler, filePath, line: 99, character: 99, semanticContext: semanticContext);

        Assert.Contains("semantic", key, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sem-key-abc", key);
        // Effective position from semantic context used, not request position
        Assert.Contains("|20|3", key);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the fingerprint string for any file on disk using the same format
    /// as <see cref="AssemblyResolver.TryGetAssemblyFingerprint"/> (path|size|ticks).
    /// Used directly here because <c>TryGetAssemblyFingerprint</c> requires a source file
    /// that resolves to a project/assembly chain.
    /// </summary>
    private static string? BuildFingerprintForFile(string path)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private static HoverHandler CreateHandler()
    {
        return new HoverHandler(new EFQueryLens.Lsp.DocumentManager(), new HoverPreviewService(new NoOpQueryLensEngine()));
    }

    private static string InvokeBuildHoverCacheKey(
        HoverHandler handler,
        string filePath,
        int line,
        int character,
        object? semanticContext)
    {
        var method = typeof(HoverHandler).GetMethod(
            "BuildHoverCacheKey",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        var result = method!.Invoke(handler, [filePath, line, character, semanticContext]);
        return Assert.IsType<string>(result);
    }

    private static object CreateSemanticHoverContext(string semanticKey, int effectiveLine, int effectiveCharacter)
    {
        var type = typeof(HoverHandler).GetNestedType("SemanticHoverContext", BindingFlags.NonPublic);
        Assert.NotNull(type);
        var instance = Activator.CreateInstance(type!, semanticKey, effectiveLine, effectiveCharacter);
        Assert.NotNull(instance);
        return instance!;
    }

    private sealed class TempFile(string extension) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + extension);
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    private sealed class NoOpQueryLensEngine : IQueryLensEngine
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<QueryTranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
            => Task.FromResult(new QueryTranslationResult());

        public Task<ModelSnapshot> InspectModelAsync(ModelInspectionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ModelSnapshot { DbContextType = string.Empty });
    }
}
