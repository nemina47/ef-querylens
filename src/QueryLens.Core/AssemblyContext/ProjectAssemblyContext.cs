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

        var ctx = new IsolatedLoadContext(AssemblyPath);
        _contextRef = new WeakReference<IsolatedLoadContext>(ctx);
        _targetAssembly = ctx.LoadFromAssemblyPath(AssemblyPath);
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
                catch { /* IsDbContextSubclass may throw for partially-loaded types */ }
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
    public Type FindDbContextType(string? typeName = null)
    {
        EnsureNotDisposed();

        var all = FindDbContextTypes();

        if (all.Count == 0)
            throw new InvalidOperationException(
                $"No DbContext subclass found in '{Path.GetFileName(AssemblyPath)}'.");

        if (typeName is null)
        {
            if (all.Count > 1)
                throw new InvalidOperationException(
                    $"Multiple DbContext types found in '{Path.GetFileName(AssemblyPath)}': " +
                    $"{string.Join(", ", all.Select(t => t.FullName))}. " +
                    "Specify --context to disambiguate.");

            return all[0];
        }

        // Match on simple name or fully-qualified name.
        var match = all.FirstOrDefault(t =>
            string.Equals(t.Name, typeName, StringComparison.Ordinal) ||
            string.Equals(t.FullName, typeName, StringComparison.Ordinal));

        return match ?? throw new InvalidOperationException(
            $"DbContext type '{typeName}' not found in '{Path.GetFileName(AssemblyPath)}'. " +
            $"Available: {string.Join(", ", all.Select(t => t.FullName))}");
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
