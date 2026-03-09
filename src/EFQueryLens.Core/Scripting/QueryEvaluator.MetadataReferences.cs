using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using EFQueryLens.Core.AssemblyContext;

namespace EFQueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
    private MetadataReference[] GetOrBuildMetadataRefs(
        ProjectAssemblyContext alcCtx,
        IReadOnlyList<Assembly> compilationAssemblies)
    {
        var setHash = ComputeAssemblySetHash(compilationAssemblies.ToArray());
        if (_refCache.TryGetValue(alcCtx.AssemblyPath, out var entry)
            && entry.AssemblyTimestamp == alcCtx.AssemblyTimestamp
            && entry.AssemblySetHash == setHash)
        {
            return entry.Refs;
        }

        var refs = CollectMetadataReferences(compilationAssemblies).ToArray();
        _refCache[alcCtx.AssemblyPath] = new MetadataRefEntry(alcCtx.AssemblyTimestamp, setHash, refs);
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

                var name = asm.GetName().Name;

                // Keep System.Linq.Queryable aligned with the major version of
                // System.Linq.Expressions to avoid mixed framework reference graphs.
                if (string.Equals(name, "System.Linq.Queryable", StringComparison.Ordinal)
                    && preferredExpressionsMajor.HasValue
                    && asm.GetName().Version?.Major is int qMajor
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

        return refs;
    }

    private static string ComputeAssemblySetHash(Assembly[] assemblies)
    {
        var sb = new StringBuilder();
        foreach (var p in assemblies.Select(a => a.Location)
                     .Where(l => !string.IsNullOrEmpty(l))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(p).Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..8];
    }
}
