using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EFQueryLens.Core.AssemblyContext;
using Microsoft.CodeAnalysis;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    private MetadataReference[] GetOrBuildMetadataRefs(
        ProjectAssemblyContext alcCtx,
        List<Assembly> compilationAssemblies,
        string assemblySetHash)
    {
        var cacheKey = $"{Path.GetFullPath(alcCtx.AssemblyPath)}|{alcCtx.AssemblyTimestamp.Ticks}|{assemblySetHash}";
        if (_refCache.TryGetValue(cacheKey, out var entry))
        {
            TouchMetadataRefCacheEntry(cacheKey, entry);
            return entry.Refs;
        }

        var refs = CollectMetadataReferences(compilationAssemblies).ToArray();
        _refCache[cacheKey] = new QueryEvaluator.MetadataRefEntry(
            refs,
            QueryEvaluator.GetUtcNowTicks());
        TrimCacheByLastAccess(_refCache, QueryEvaluator.MaxMetadataRefCacheEntries, static e => e.LastAccessTicks);
        return refs;
    }

    private static IEnumerable<MetadataReference> CollectMetadataReferences(IEnumerable<Assembly> assemblies)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var refs = new List<MetadataReference>();

        var expressionsAssembly = assemblies.FirstOrDefault(a =>
            string.Equals(a.GetName().Name, "System.Linq.Expressions", StringComparison.Ordinal));
        var preferredExpressionsMajor = expressionsAssembly?.GetName().Version?.Major;
        var expressionsDir = expressionsAssembly is null
            ? null
            : Path.GetDirectoryName(expressionsAssembly.Location);

        foreach (var asm in assemblies)
        {
            try
            {
                var loc = asm.Location;
                if (string.IsNullOrEmpty(loc) || !seen.Add(loc))
                    continue;

                // Skip RID-specific runtime assets (runtimes/<rid>/...).
                // For Roslyn compilation, these can introduce duplicate/alternate
                // metadata graphs that surface internal provider implementation types
                // (for example SqlClient SNI internals) as CS0122 errors.
                var normalizedLoc = loc.Replace('\\', '/');
                if (normalizedLoc.Contains("/runtimes/", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[QL-Eval] metadata-ref-skip-rid location={loc}");
                    continue;
                }

                var name = asm.GetName().Name;
                if (ShouldSkipMetadataReferenceAssembly(name))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[QL-Eval] metadata-ref-skip-compiler name={name} location={loc}");
                    continue;
                }

                // Keep System.Linq.Queryable aligned with the major version of
                // System.Linq.Expressions to avoid mixed framework reference graphs.
                if (string.Equals(name, "System.Linq.Queryable", StringComparison.Ordinal)
                    && preferredExpressionsMajor.HasValue
                    && asm.GetName().Version?.Major is { } qMajor
                    && qMajor != preferredExpressionsMajor.Value)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(name))
                    seenNames.Add(name);

                refs.Add(MetadataReference.CreateFromFile(loc));
            }
            catch
            {
                // Skip dynamic or in-memory assemblies with no file location.
            }
        }

        // If System.Linq.Queryable wasn't loaded yet, try to add it from the same
        // framework directory as System.Linq.Expressions to keep versions aligned.
        if (!seenNames.Contains("System.Linq.Queryable") && !string.IsNullOrWhiteSpace(expressionsDir))
        {
            var candidate = Path.Combine(expressionsDir, "System.Linq.Queryable.dll");
            if (File.Exists(candidate) && seen.Add(candidate))
            {
                refs.Add(MetadataReference.CreateFromFile(candidate));
            }
        }

        // Bring in System.Data references only when missing, and align to the same
        // framework directory as System.Linq.Expressions to avoid mixed-reference graphs.
        EnsureFrameworkReferenceBySimpleName("System.Data.Common", expressionsDir, refs, seen, seenNames);
        EnsureFrameworkReferenceBySimpleName("System.Data", expressionsDir, refs, seen, seenNames);

        return refs;
    }

    private static bool ShouldSkipMetadataReferenceAssembly(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return false;

        // Compiler/analyzer assemblies are not needed for script compilation and can
        // surface internal Roslyn types (for example NullableWalker) as CS0122 diagnostics.
        return assemblyName.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal);
    }

    private static void EnsureFrameworkReferenceBySimpleName(
        string simpleName,
        string? frameworkDir,
        ICollection<MetadataReference> refs,
        ISet<string> seenPaths,
        ISet<string> seenNames)
    {
        if (seenNames.Contains(simpleName) || string.IsNullOrWhiteSpace(frameworkDir))
            return;

        var candidate = Path.Combine(frameworkDir, $"{simpleName}.dll");
        if (!File.Exists(candidate) || !seenPaths.Add(candidate))
            return;

        refs.Add(MetadataReference.CreateFromFile(candidate));
        seenNames.Add(simpleName);
    }

    private static string ComputeAssemblySetHash(List<Assembly> assemblies)
    {
        var sb = new StringBuilder();
        foreach (var asm in assemblies
                     .Where(ShouldIncludeInAssemblySetHash)
                     .OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            var name = asm.GetName().Name ?? string.Empty;
            string mvid;
            try
            {
                mvid = asm.ManifestModule.ModuleVersionId.ToString("N");
            }
            catch
            {
                mvid = asm.FullName ?? string.Empty;
            }

            sb.Append(name).Append('|').Append(mvid).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..8];
    }

    private static bool ShouldIncludeInAssemblySetHash(Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name;
        if (!string.IsNullOrWhiteSpace(assemblyName)
            && assemblyName.StartsWith("__QueryLensEval_", StringComparison.Ordinal))
        {
            return false;
        }

        var assemblyPath = assembly.Location;
        var fileName = Path.GetFileNameWithoutExtension(assemblyPath);
        if (fileName.StartsWith("__QueryLensEval_", StringComparison.Ordinal))
            return false;

        return true;
    }
}
