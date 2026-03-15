using System.Reflection;
using System.Runtime.Loader;

namespace EFQueryLens.Core.AssemblyContext;

public sealed partial class ProjectAssemblyContext
{
    /// <summary>
    /// The collectible AssemblyLoadContext. Kept private and nested so callers
    /// interact only through the ProjectAssemblyContext facade.
    /// </summary>
    private sealed partial class IsolatedLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _assemblyDirectory;
        private readonly string[] _runtimeRidProbeOrder;
        private readonly string[] _sharedFrameworkProbeDirs;

        /// <param name="assemblyPath">
        ///   Full path to the primary assembly. The resolver uses this to
        ///   locate the .deps.json and runtimeconfig.json produced by the
        ///   build, enabling accurate dependency resolution.
        /// </param>
        public IsolatedLoadContext(string assemblyPath)
            : base(
                name: $"QueryLens-{Path.GetFileNameWithoutExtension(assemblyPath)}",
                isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
            _assemblyDirectory = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
            _runtimeRidProbeOrder = BuildRuntimeRidProbeOrder();
            _sharedFrameworkProbeDirs = BuildSharedFrameworkProbeDirs(assemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (ShouldPreferDefaultLoadContext(assemblyName.Name))
                return null;

            // Prefer runtime binaries copied to the target output directory.
            // Some resolvers can return package ref-assemblies (e.g. .../ref/netX/...)
            // which compile but throw PlatformNotSupportedException at runtime.
            if (!string.IsNullOrWhiteSpace(assemblyName.Name))
            {
                var ridRuntimeCandidate = TryResolveRidRuntimeAssemblyPath(assemblyName.Name);
                if (!string.IsNullOrWhiteSpace(ridRuntimeCandidate))
                    return LoadFromAssemblyPath(ridRuntimeCandidate);

                var localCandidate = Path.Combine(_assemblyDirectory, assemblyName.Name + ".dll");
                if (File.Exists(localCandidate))
                    return LoadFromAssemblyPath(localCandidate);
            }

            // Always try to resolve from the user project's deps.json first.
            var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            if (resolved is not null)
            {
                if (!LooksLikeReferenceAssemblyPath(resolved))
                    return LoadFromAssemblyPath(resolved);

                if (!string.IsNullOrWhiteSpace(assemblyName.Name))
                {
                    var runtimeCandidate = TryResolveRuntimeAssemblyPathFromReference(
                        resolved,
                        assemblyName.Name);
                    if (!string.IsNullOrWhiteSpace(runtimeCandidate))
                        return LoadFromAssemblyPath(runtimeCandidate);
                }
            }

            // Fallback 1: if resolver returned a ref assembly path, probe shared frameworks.
            // Fallback 2: probe installed shared frameworks (Microsoft.NETCore.App,
            // Microsoft.AspNetCore.App, etc.) based on the target runtimeconfig.
            if (!string.IsNullOrWhiteSpace(assemblyName.Name))
            {
                foreach (var probeDir in _sharedFrameworkProbeDirs)
                {
                    var sharedCandidate = Path.Combine(probeDir, assemblyName.Name + ".dll");
                    if (File.Exists(sharedCandidate))
                        return LoadFromAssemblyPath(sharedCandidate);
                }
            }

            // Fall back to the default ALC for framework / shared assemblies.
            return null;
        }

        private static bool LooksLikeReferenceAssemblyPath(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                return false;

            var normalized = assemblyPath.Replace('\\', '/');
            return normalized.Contains("/ref/", StringComparison.OrdinalIgnoreCase);
        }

        internal static string? TryResolveRuntimeAssemblyPathFromReference(
            string referenceAssemblyPath,
            string assemblySimpleName)
        {
            if (string.IsNullOrWhiteSpace(referenceAssemblyPath)
                || string.IsNullOrWhiteSpace(assemblySimpleName))
            {
                return null;
            }

            try
            {
                var normalized = referenceAssemblyPath.Replace('\\', '/');
                var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var refIndex = Array.FindIndex(parts,
                    p => string.Equals(p, "ref", StringComparison.OrdinalIgnoreCase));

                if (refIndex <= 0 || refIndex >= parts.Length - 1)
                    return null;

                var packageRoot = string.Join(Path.DirectorySeparatorChar, parts.Take(refIndex));
                var tfm = parts[refIndex + 1];
                var fileName = assemblySimpleName + ".dll";

                var directTfmCandidate = Path.Combine(packageRoot, "lib", tfm, fileName);
                if (File.Exists(directTfmCandidate))
                    return directTfmCandidate;

                var libRoot = Path.Combine(packageRoot, "lib");
                if (!Directory.Exists(libRoot))
                    return null;

                var candidates = Directory.EnumerateFiles(libRoot, fileName, SearchOption.AllDirectories)
                    .Where(path => !LooksLikeReferenceAssemblyPath(path))
                    .OrderByDescending(path => path.Contains(Path.DirectorySeparatorChar + tfm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return candidates.Length > 0 ? candidates[0] : null;
            }
            catch
            {
                return null;
            }
        }

        internal string NormalizeAssemblyPathForLoad(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return fullPath;

            try
            {
                var candidateDir = Path.GetDirectoryName(fullPath);
                if (!string.Equals(candidateDir, _assemblyDirectory, StringComparison.OrdinalIgnoreCase))
                    return fullPath;

                var assemblySimpleName = Path.GetFileNameWithoutExtension(fullPath);
                if (string.IsNullOrWhiteSpace(assemblySimpleName))
                    return fullPath;

                var ridRuntimeCandidate = TryResolveRidRuntimeAssemblyPath(assemblySimpleName);
                if (!string.IsNullOrWhiteSpace(ridRuntimeCandidate))
                    return ridRuntimeCandidate;

                return fullPath;
            }
            catch
            {
                return fullPath;
            }
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return resolved is not null
                ? LoadUnmanagedDllFromPath(resolved)
                : IntPtr.Zero;
        }
    }
}
