using System.Reflection;
using System.Runtime.Loader;

namespace QueryLens.Core.AssemblyContext;

/// <summary>
/// An isolated, collectible AssemblyLoadContext that loads a user's compiled
/// assembly and all of its dependencies from the same output directory.
///
/// Key invariant: the user's EF Core / provider assemblies are loaded here,
/// NOT shared with the tool's own runtime. This prevents version conflicts
/// when the user's project targets a different EF Core version than QueryLens.
/// </summary>
public sealed class ProjectAssemblyContext : IDisposable
{
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
            // Skip assemblies that IsolatedLoadContext.Load defers to the default ALC.
            // Forcing these via LoadFromAssemblyPath bypasses the Load override and causes
            // a version conflict between the user-bin EF Core and the host's shared EF Core.
            var name = Path.GetFileNameWithoutExtension(dll);
            if (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Pomelo.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
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

        return ctx.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
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
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Phase 1: Share EF Core and Pomelo with the host's default ALC so
            // that DbContextOptionsBuilder<T> casts succeed at the IProviderBootstrap
            // boundary (both sides see the same runtime type identity for EF Core).
            //
            // Limitation: the tool and user app must use the same EF Core major
            // version. Phase 2 will remove this guard and support cross-version
            // isolation via a reflection-only bootstrap protocol.
            if (assemblyName.Name is { } name &&
                (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                 name.StartsWith("Pomelo.EntityFrameworkCore", StringComparison.Ordinal)))
            {
                return null; // defer to the default ALC
            }

            // Ask the deps.json-based resolver for user-project assemblies.
            // This respects the exact versions pinned by the user's project.
            var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            if (resolved is not null)
                return LoadFromAssemblyPath(resolved);

            // Fall back to the default (shared) context for framework assemblies
            // (System.*, Microsoft.Extensions.*, etc.) that don't need isolation.
            return null;
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
