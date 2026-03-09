using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace EFQueryLens.Core.AssemblyContext;

/// <summary>
/// An isolated, collectible AssemblyLoadContext that loads a user's compiled
/// assembly and all of its dependencies from the same output directory.
///
/// Key invariant: the user's EF Core / provider assemblies are loaded here,
/// NOT shared with the tool's own runtime. This prevents version conflicts
/// when the user's project targets a different EF Core version than EFQueryLens.
/// </summary>
public sealed class ProjectAssemblyContext : IDisposable
{
    private static readonly string[] s_defaultLoadContextPrefixes =
    [
        "Microsoft.Build",
        "NuGet.",
        "Microsoft.VisualStudio.",
        "Microsoft.CodeAnalysis.Workspaces.MSBuild",
        "Microsoft.TestPlatform",
        "testhost",
    ];

    // We hold the ALC in a WeakReference so that after Dispose() + GC the ALC
    // can actually be collected. Collectible ALCs are GC-rooted only by the
    // types loaded into them — not by strong CLR handles.
    private readonly WeakReference<IsolatedLoadContext> _contextRef;
    private Assembly? _targetAssembly;
    private bool _disposed;

    /// <summary>Absolute path to the primary assembly that was loaded.</summary>
    public string AssemblyPath { get; }

    /// <summary>UTC last-write time of the assembly file at load time.</summary>
    public DateTime AssemblyTimestamp { get; }

    /// <param name="assemblyPath">Absolute path to the user's compiled .dll.</param>
    public ProjectAssemblyContext(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException($"Assembly not found: {assemblyPath}", assemblyPath);

        AssemblyPath = Path.GetFullPath(assemblyPath);
        AssemblyTimestamp = File.GetLastWriteTimeUtc(AssemblyPath);
        EnsureRuntimeConfigDevExists(AssemblyPath);

        var ctx = new IsolatedLoadContext(AssemblyPath);
        _contextRef = new WeakReference<IsolatedLoadContext>(ctx);
        _targetAssembly = ctx.LoadFromAssemblyPath(AssemblyPath);

        EagerLoadBinDirAssemblies();
    }

    /// <summary>
    /// Class libraries do not generate a .runtimeconfig.dev.json file by default.
    /// AssemblyDependencyResolver requires this file to know where the NuGet package
    /// cache is located. If it is missing, we generate a dummy one pointing to the
    /// standard NuGet cache so that all third-party dependencies can be resolved.
    /// </summary>
    private static void EnsureRuntimeConfigDevExists(string assemblyPath)
    {
        var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        var devConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.dev.json");

        if (!File.Exists(runtimeConfigPath))
        {
            try
            {
                // Create a generic runtimeconfig.json so AssemblyDependencyResolver doesn't abort.
                // It just needs to exist for the dev.json to be processed.
                var baseJson = """
                               {
                                 "runtimeOptions": {
                                   "tfm": "net8.0",
                                   "framework": {
                                     "name": "Microsoft.NETCore.App",
                                     "version": "8.0.0"
                                   }
                                 }
                               }
                               """;
                File.WriteAllText(runtimeConfigPath, baseJson);
            }
            catch
            {
            }
        }

        if (!File.Exists(devConfigPath))
        {
            try
            {
                var nugetCache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
                if (string.IsNullOrEmpty(nugetCache))
                {
                    nugetCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".nuget", "packages");
                }

                nugetCache = nugetCache.Replace("\\", "\\\\");

                var devJson = $$"""
                                {
                                  "runtimeOptions": {
                                    "additionalProbingPaths": [
                                      "{{nugetCache}}"
                                    ]
                                  }
                                }
                                """;

                File.WriteAllText(devConfigPath, devJson);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Eagerly loads every <c>.dll</c> found in the same directory as the primary
    /// assembly into the user's isolated ALC. This ensures that lazy-loaded transitive
    /// dependencies are present in <see cref="LoadedAssemblies"/>
    /// so that DbContext subclasses in separate class libraries are discoverable,
    /// and Roslyn sees the full set of extension methods at script-compilation time.
    /// </summary>
    private void EagerLoadBinDirAssemblies()
    {
        var binDir = Path.GetDirectoryName(AssemblyPath);
        if (string.IsNullOrEmpty(binDir) || !Directory.Exists(binDir))
            return;

        foreach (var dll in Directory.EnumerateFiles(binDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var assemblyName = Path.GetFileNameWithoutExtension(dll);
            if (ShouldPreferDefaultLoadContext(assemblyName))
                continue;

            try
            {
                LoadAdditionalAssembly(dll);
            }
            catch
            {
                // Best-effort: some dlls may be native or otherwise unloadable — skip them.
            }
        }
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Explicitly loads an additional assembly into the isolated ALC using the
    /// same <see cref="AssemblyDependencyResolver"/> as the primary assembly.
    /// Use this when the DbContext lives in a class library rather than the
    /// primary executable assembly (e.g. load the API dll as primary for its
    /// deps.json, then call this to pre-load the Core class library so that
    /// <see cref="FindDbContextTypes"/> can discover the DbContext).
    /// </summary>
    public Assembly LoadAdditionalAssembly(string assemblyPath)
    {
        EnsureNotDisposed();

        if (!_contextRef.TryGetTarget(out var ctx))
            throw new ObjectDisposedException(nameof(ProjectAssemblyContext));

        var fullPath = Path.GetFullPath(assemblyPath);
        fullPath = ctx.NormalizeAssemblyPathForLoad(fullPath);
        return ctx.LoadFromAssemblyPath(fullPath);
    }

    /// <summary>
    /// Loads a compiled in-memory assembly (emitted by <c>CSharpCompilation.Emit</c>)
    /// into this isolated ALC. The assembly executes in the same type-identity space
    /// as the user's project assemblies, so casts to user EF Core entity types succeed
    /// regardless of which EF Core major version the user's project targets.
    /// </summary>
    public Assembly LoadEvalAssembly(Stream stream)
    {
        EnsureNotDisposed();

        if (!_contextRef.TryGetTarget(out var ctx))
            throw new ObjectDisposedException(nameof(ProjectAssemblyContext));

        return ctx.LoadFromStream(stream);
    }

    /// <summary>
    /// Returns all concrete (non-abstract) DbContext subclasses found across
    /// <b>all assemblies</b> currently loaded into this context (including any
    /// additional assemblies pre-loaded via <see cref="LoadAdditionalAssembly"/>).
    /// Walks the full inheritance chain by type name, because the DbContext type
    /// in the user's ALC is a different runtime instance than the one in the
    /// tool's default load context.
    /// </summary>
    public IReadOnlyList<Type> FindDbContextTypes()
    {
        EnsureNotDisposed();

        var results = new List<Type>();

        // Scan ALL loaded assemblies — not just the primary target — so that
        // projects which place DbContext in a class library dependency are
        // discovered correctly after LoadAdditionalAssembly() is called.
        foreach (var asm in LoadedAssemblies)
        {
            IEnumerable<Type> candidates;
            try
            {
                candidates = asm.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                // Some types in the assembly couldn't be loaded (e.g. a factory
                // class that references a design-time interface whose assembly is
                // not in the probe paths).  The successfully loaded types are still
                // in rtle.Types — use them so that DbContext types aren't missed.
                candidates = rtle.Types.Where(t => t is not null)!;
            }
            catch
            {
                // Truly broken assembly — skip entirely.
                continue;
            }

            foreach (var type in candidates)
            {
                try
                {
                    if (!type.IsAbstract && IsDbContextSubclass(type))
                        results.Add(type);
                }
                catch
                {
                    /* IsDbContextSubclass may throw for partially-loaded types */
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Resolves a single DbContext type from the loaded assembly.
    /// </summary>
    /// <param name="typeName">
    ///   Simple name ("AppDbContext") or fully qualified name
    ///   ("SampleApp.AppDbContext"). Pass null to auto-discover when exactly
    ///   one DbContext exists in the assembly.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///   No DbContext found; multiple found with null typeName; or no match
    ///   for the provided typeName.
    /// </exception>
    public Type FindDbContextType(string? typeName = null, string? expressionHint = null)
    {
        EnsureNotDisposed();

        var all = FindDbContextTypes();

        if (all.Count == 0)
            throw new InvalidOperationException(
                $"No DbContext subclass found in '{Path.GetFileName(AssemblyPath)}'.");

        if (typeName is null)
        {
            if (all.Count == 1)
                return all[0];

            // Auto-disambiguate using the LINQ expression: extract the first property
            // access (e.g. "AppWorkflows" from "dbContext.AppWorkflows.Include(...)") and
            // find which DbContext owns a DbSet/IQueryable property with that name.
            if (expressionHint is not null)
            {
                var dbSetName = ExtractFirstPropertyAccess(expressionHint);
                if (dbSetName is not null)
                {
                    var match = all.FirstOrDefault(t =>
                        t.GetProperties().Any(p =>
                            string.Equals(p.Name, dbSetName, StringComparison.Ordinal)));

                    if (match is not null)
                        return match;
                }
            }

            // Fallback: filter out obvious test/utility DbContexts
            var filtered = all.Where(t =>
            {
                var name = t.Name;
                return !name.Contains("Test", StringComparison.OrdinalIgnoreCase) &&
                       !name.Contains("Empty", StringComparison.OrdinalIgnoreCase) &&
                       !name.Contains("Mock", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (filtered.Count == 1)
                return filtered[0];

            var candidates = filtered.Count > 1 ? filtered : all;
            throw new InvalidOperationException(
                $"Multiple DbContext types found in '{Path.GetFileName(AssemblyPath)}': " +
                $"{string.Join(", ", candidates.Select(t => t.FullName))}. " +
                "Specify --context to disambiguate.");
        }

        // Match on simple name or fully-qualified name.
        var nameMatch = all.FirstOrDefault(t =>
            string.Equals(t.Name, typeName, StringComparison.Ordinal) ||
            string.Equals(t.FullName, typeName, StringComparison.Ordinal));

        return nameMatch ?? throw new InvalidOperationException(
            $"DbContext type '{typeName}' not found in '{Path.GetFileName(AssemblyPath)}'. " +
            $"Available: {string.Join(", ", all.Select(t => t.FullName))}");
    }

    /// <summary>
    /// Extracts the first member access from a LINQ expression.
    /// e.g. "dbContext.AppWorkflows.Include(...)" → "AppWorkflows"
    ///      "db.Orders.Where(...)" → "Orders"
    /// </summary>
    private static string? ExtractFirstPropertyAccess(string expression)
    {
        // Trim leading whitespace and the variable name prefix (e.g. "dbContext." or "db.")
        var trimmed = expression.TrimStart();

        // Find the first dot — everything after is the property chain
        var firstDot = trimmed.IndexOf('.');
        if (firstDot < 0 || firstDot >= trimmed.Length - 1)
            return null;

        var afterDot = trimmed[(firstDot + 1)..].TrimStart();

        // The property name is everything up to the next dot, paren, or whitespace
        var endIndex = 0;
        while (endIndex < afterDot.Length &&
               char.IsLetterOrDigit(afterDot[endIndex]) || afterDot[endIndex] == '_')
        {
            endIndex++;
        }

        return endIndex > 0 ? afterDot[..endIndex] : null;
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────

    /// <summary>
    /// Unloads the isolated ALC and releases all loaded assemblies.
    /// After disposal, FindDbContextType* calls throw ObjectDisposedException.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _targetAssembly = null;

        if (_contextRef.TryGetTarget(out var ctx))
            ctx.Unload();
    }

    /// <summary>
    /// Opens a contextual reflection scope on the user's isolated ALC.
    /// Wrap Roslyn script compilation in this to ensure any Assembly.Load
    /// calls inside the compiler resolve relative to the user's ALC.
    /// </summary>
    public IDisposable EnterContextualReflection()
    {
        EnsureNotDisposed();
        return _contextRef.TryGetTarget(out var ctx)
            ? ctx.EnterContextualReflection()
            : AssemblyLoadContext.Default.EnterContextualReflection();
    }

    /// <summary>
    /// All assemblies currently loaded into the user's isolated ALC.
    /// Used by <c>QueryEvaluator</c> to build Roslyn MetadataReferences
    /// so the script can resolve entity types and DbSet members.
    /// </summary>
    public IEnumerable<Assembly> LoadedAssemblies
    {
        get
        {
            EnsureNotDisposed();
            return _contextRef.TryGetTarget(out var ctx)
                ? ctx.Assemblies
                : Enumerable.Empty<Assembly>();
        }
    }

    /// <summary>
    /// Returns a non-generic WeakReference to the inner ALC for unload-
    /// verification in tests. Callers must null all strong refs and force GC
    /// after Dispose() to observe IsAlive → false.
    /// </summary>
    internal WeakReference GetAlcWeakReference()
    {
        _contextRef.TryGetTarget(out var ctx);
        return new WeakReference(ctx);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void EnsureNotDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    internal static bool ShouldPreferDefaultLoadContext(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return false;

        foreach (var prefix in s_defaultLoadContextPrefixes)
        {
            if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static string? TryResolveRuntimeAssemblyPathFromReferencePath(
        string referenceAssemblyPath,
        string assemblySimpleName) =>
        IsolatedLoadContext.TryResolveRuntimeAssemblyPathFromReference(
            referenceAssemblyPath,
            assemblySimpleName);

    /// <summary>
    /// Walks the base-type chain by FullName to detect DbContext subclasses.
    /// Name-based comparison is required because the DbContext type loaded
    /// inside the ALC is a different runtime type than typeof(DbContext) in
    /// the host process — even though they are semantically the same class.
    /// </summary>
    private static bool IsDbContextSubclass(Type type)
    {
        const string dbContextFullName = "Microsoft.EntityFrameworkCore.DbContext";
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.FullName == dbContextFullName)
                return true;
            current = current.BaseType;
        }

        return false;
    }

    // ─── Inner ALC ───────────────────────────────────────────────────────────

    /// <summary>
    /// The collectible AssemblyLoadContext. Kept private and nested so callers
    /// interact only through the ProjectAssemblyContext façade.
    /// </summary>
    private sealed class IsolatedLoadContext : AssemblyLoadContext
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
            // Some resolvers can return package ref-assemblies (e.g. .../ref/netX/...),
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
            // This ensures the user's exact EF Core version (and all provider-specific
            // assemblies like EntityFrameworkCore.Projectables) are loaded from their
            // bin directory, preventing cross-version type identity conflicts when the
            // user's project targets a different EF Core major version than EFQueryLens.
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

            // Fall back to the default ALC for framework / shared assemblies
            // (System.*, netstandard, etc.) not present in the user's bin.
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

        internal string? TryResolveRidRuntimeAssemblyPath(string assemblySimpleName)
        {
            if (string.IsNullOrWhiteSpace(assemblySimpleName))
                return null;

            var runtimesRoot = Path.Combine(_assemblyDirectory, "runtimes");
            if (!Directory.Exists(runtimesRoot))
                return null;

            var fileName = assemblySimpleName + ".dll";
            var candidates = new List<(string Path, int RidScore, int TfmScore)>();

            try
            {
                foreach (var path in Directory.EnumerateFiles(runtimesRoot, fileName, SearchOption.AllDirectories))
                {
                    var rid = TryExtractRid(path);
                    if (string.IsNullOrWhiteSpace(rid))
                        continue;

                    var tfm = TryExtractTfm(path);
                    candidates.Add((
                        path,
                        GetRidScore(rid),
                        GetTfmScore(tfm)));
                }
            }
            catch
            {
                return null;
            }

            if (candidates.Count == 0)
                return null;

            return candidates
                .OrderBy(c => c.RidScore)
                .ThenByDescending(c => c.TfmScore)
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.Path)
                .FirstOrDefault();
        }

        private int GetRidScore(string rid)
        {
            for (var i = 0; i < _runtimeRidProbeOrder.Length; i++)
            {
                if (string.Equals(_runtimeRidProbeOrder[i], rid, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return int.MaxValue;
        }

        private static int GetTfmScore(string? tfm)
        {
            if (string.IsNullOrWhiteSpace(tfm))
                return 0;

            if (tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            {
                var versionText = tfm["netstandard".Length..];
                if (Version.TryParse(versionText, out var parsed))
                    return 1000 + (parsed.Major * 10) + parsed.Minor;
                return 1000;
            }

            if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            {
                var versionText = tfm[3..];
                if (Version.TryParse(versionText, out var parsed))
                    return 2000 + (parsed.Major * 10) + parsed.Minor;
                if (int.TryParse(versionText, out var majorOnly))
                    return 2000 + (majorOnly * 10);
                return 2000;
            }

            return 0;
        }

        private static string? TryExtractRid(string path)
        {
            var normalized = path.Replace('\\', '/');
            const string marker = "/runtimes/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return null;

            var start = markerIndex + marker.Length;
            var end = normalized.IndexOf('/', start);
            if (end <= start)
                return null;

            return normalized[start..end];
        }

        private static string? TryExtractTfm(string path)
        {
            var normalized = path.Replace('\\', '/');
            const string marker = "/lib/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return null;

            var start = markerIndex + marker.Length;
            var end = normalized.IndexOf('/', start);
            if (end <= start)
                return null;

            return normalized[start..end];
        }

        private static string[] BuildRuntimeRidProbeOrder()
        {
            var rids = new List<string>();

            if (OperatingSystem.IsWindows())
            {
                AddArchRid(rids, "win");
                rids.Add("win");
            }
            else if (OperatingSystem.IsLinux())
            {
                AddArchRid(rids, "linux");
                rids.Add("linux");
                rids.Add("unix");
            }
            else if (OperatingSystem.IsMacOS())
            {
                AddArchRid(rids, "osx");
                rids.Add("osx");
                rids.Add("unix");
            }
            else
            {
                rids.Add("unix");
            }

            return rids
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void AddArchRid(ICollection<string> rids, string baseRid)
        {
            var archRid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => baseRid + "-x64",
                Architecture.X86 => baseRid + "-x86",
                Architecture.Arm64 => baseRid + "-arm64",
                Architecture.Arm => baseRid + "-arm",
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(archRid))
                rids.Add(archRid);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return resolved is not null
                ? LoadUnmanagedDllFromPath(resolved)
                : IntPtr.Zero;
        }

        private static string[] BuildSharedFrameworkProbeDirs(string assemblyPath)
        {
            var frameworkRequests = ReadRuntimeFrameworkRequests(assemblyPath);
            if (frameworkRequests.Count == 0)
            {
                frameworkRequests.Add(("Microsoft.NETCore.App", null));
                frameworkRequests.Add(("Microsoft.AspNetCore.App", null));
            }

            var roots = GetDotnetRoots();
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                var sharedRoot = Path.Combine(root, "shared");
                if (!Directory.Exists(sharedRoot))
                    continue;

                foreach (var req in frameworkRequests)
                {
                    var frameworkBase = Path.Combine(sharedRoot, req.Name);
                    var selected = SelectFrameworkVersionDir(frameworkBase, req.Version);
                    if (!string.IsNullOrEmpty(selected))
                        dirs.Add(selected);
                }
            }

            return dirs.ToArray();
        }

        private static List<(string Name, string? Version)> ReadRuntimeFrameworkRequests(string assemblyPath)
        {
            var result = new List<(string Name, string? Version)>();
            var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
            if (!File.Exists(runtimeConfigPath))
                return result;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
                if (!doc.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions))
                    return result;

                if (runtimeOptions.TryGetProperty("framework", out var frameworkObj))
                    TryReadFramework(frameworkObj, result);

                if (runtimeOptions.TryGetProperty("frameworks", out var frameworksArray)
                    && frameworksArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var framework in frameworksArray.EnumerateArray())
                        TryReadFramework(framework, result);
                }
            }
            catch
            {
                // Best-effort runtime probing only.
            }

            return result;
        }

        private static void TryReadFramework(
            JsonElement framework,
            ICollection<(string Name, string? Version)> target)
        {
            if (!framework.TryGetProperty("name", out var nameProp)
                || nameProp.ValueKind != JsonValueKind.String)
                return;

            var name = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(name))
                return;

            string? version = null;
            if (framework.TryGetProperty("version", out var versionProp)
                && versionProp.ValueKind == JsonValueKind.String)
                version = versionProp.GetString();

            target.Add((name, version));
        }

        private static string? SelectFrameworkVersionDir(string frameworkBasePath, string? requestedVersion)
        {
            if (!Directory.Exists(frameworkBasePath))
                return null;

            if (!string.IsNullOrWhiteSpace(requestedVersion))
            {
                var exact = Path.Combine(frameworkBasePath, requestedVersion);
                if (Directory.Exists(exact))
                    return exact;
            }

            var candidates = Directory.EnumerateDirectories(frameworkBasePath)
                .Select(d => new
                {
                    Path = d,
                    Name = Path.GetFileName(d),
                })
                .Select(x => new
                {
                    x.Path,
                    Parsed = Version.TryParse(x.Name, out var v) ? v : null,
                })
                .Where(x => x.Parsed is not null)
                .ToList();

            if (candidates.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(requestedVersion)
                && Version.TryParse(requestedVersion, out var requested))
            {
                var sameTrain = candidates
                    .Where(c => c.Parsed!.Major == requested.Major && c.Parsed.Minor == requested.Minor)
                    .OrderByDescending(c => c.Parsed)
                    .FirstOrDefault();

                if (sameTrain is not null)
                    return sameTrain.Path;
            }

            return candidates
                .OrderByDescending(c => c.Parsed)
                .First().Path;
        }

        private static IReadOnlyCollection<string> GetDotnetRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
                roots.Add(envRoot);

            var envRootX86 = Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)");
            if (!string.IsNullOrWhiteSpace(envRootX86) && Directory.Exists(envRootX86))
                roots.Add(envRootX86);

            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                var processDir = Path.GetDirectoryName(Environment.ProcessPath);
                if (!string.IsNullOrWhiteSpace(processDir) && Directory.Exists(processDir))
                    roots.Add(processDir);
            }

            if (OperatingSystem.IsWindows())
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var defaultRoot = Path.Combine(programFiles, "dotnet");
                if (Directory.Exists(defaultRoot))
                    roots.Add(defaultRoot);

                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var defaultRootX86 = Path.Combine(programFilesX86, "dotnet");
                if (Directory.Exists(defaultRootX86))
                    roots.Add(defaultRootX86);
            }

            return roots;
        }
    }
}
