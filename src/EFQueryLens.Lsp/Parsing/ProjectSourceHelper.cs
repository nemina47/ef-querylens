using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EFQueryLens.Lsp.Parsing;

/// <summary>
/// Provides a short-lived cache of parsed SyntaxNode roots for all .cs files in a project,
/// used for cross-file method declaration lookup (e.g. Expression parameter helper synthesis).
/// </summary>
internal static class ProjectSourceHelper
{
    private sealed record CachedProjectRoots(IReadOnlyList<SyntaxNode> Roots, long ExpiresAtUtcTicks);

    private static readonly ConcurrentDictionary<string, CachedProjectRoots> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    // 10-second TTL: cheap enough to be effectively instant, long enough to cover
    // rapid re-hovers as the user moves around the same file.
    private static readonly long TtlTicks = TimeSpan.FromSeconds(10).Ticks;

    // Hard cap on the number of source files we parse per project to bound memory/time.
    private const int MaxFilesPerProject = 500;

    /// <summary>
    /// Returns parsed SyntaxNode roots for all .cs files in the project that contains
    /// <paramref name="currentFilePath"/>, excluding the current file itself (which the
    /// caller already holds as its own root).  Results are cached for 10 seconds.
    /// </summary>
    public static IReadOnlyList<SyntaxNode> GetSiblingRoots(string currentFilePath)
    {
        var projectDir = AssemblyResolver.TryGetProjectDirectory(currentFilePath);
        if (string.IsNullOrEmpty(projectDir))
            return [];

        var now = DateTime.UtcNow.Ticks;
        if (Cache.TryGetValue(projectDir, out var cached) && cached.ExpiresAtUtcTicks > now)
            return cached.Roots;

        var normalizedCurrentPath = Path.GetFullPath(currentFilePath);

        var roots = new List<SyntaxNode>();
        foreach (var file in Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            if (roots.Count >= MaxFilesPerProject)
                break;

            if (string.Equals(Path.GetFullPath(file), normalizedCurrentPath, StringComparison.OrdinalIgnoreCase))
                continue; // already searched as the primary root

            try
            {
                roots.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot());
            }
            catch
            {
                // unparseable file — skip silently
            }
        }

        var result = new CachedProjectRoots((IReadOnlyList<SyntaxNode>)roots, now + TtlTicks);
        Cache[projectDir] = result;
        return result.Roots;
    }
}
