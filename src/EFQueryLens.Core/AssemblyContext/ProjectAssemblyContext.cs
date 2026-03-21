using System.Reflection;
using System.Runtime.Loader;

namespace EFQueryLens.Core.AssemblyContext;

/// <summary>
/// An isolated, collectible AssemblyLoadContext that loads a user's compiled
/// assembly and all of its dependencies from the same output directory.
///
/// Key invariant: the user's EF Core / provider assemblies are loaded here,
/// NOT shared with the tool's own runtime. This prevents version conflicts
/// when the user's project targets a different EF Core version than EFQueryLens.
/// </summary>
public sealed partial class ProjectAssemblyContext : IDisposable
{
    private static readonly string[] SDefaultLoadContextPrefixes =
    [
        "Microsoft.Build",
        "NuGet.",
        "Microsoft.VisualStudio.",
        "Microsoft.CodeAnalysis.Workspaces.MSBuild",
        "Microsoft.TestPlatform",
        "testhost",
    ];

    // Strong reference keeps the ALC alive as long as this context is alive.
    // Cleared in Dispose() to allow the collectible ALC to be GC'd after unload.
    private IsolatedLoadContext? _ctx;
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
        EnsureExecutableAssemblyArtifactsExist(AssemblyPath);
        AssemblyTimestamp = File.GetLastWriteTimeUtc(AssemblyPath);

        _ctx = new IsolatedLoadContext(AssemblyPath);
        _ctx.LoadFromAssemblyPath(AssemblyPath);

        EagerLoadBinDirAssemblies();
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

        if (_ctx is not {} ctx)
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

        if (_ctx is not {} ctx)
            throw new ObjectDisposedException(nameof(ProjectAssemblyContext));

        return ctx.LoadFromStream(stream);
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

        _ctx?.Unload();
        _ctx = null;
    }

    /// <summary>
    /// Opens a contextual reflection scope on the user's isolated ALC.
    /// Wrap Roslyn script compilation in this to ensure any Assembly.Load
    /// calls inside the compiler resolve relative to the user's ALC.
    /// </summary>
    public IDisposable EnterContextualReflection()
    {
        EnsureNotDisposed();
        return _ctx is {} ctx
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
            return _ctx is {} ctx
                ? ctx.Assemblies
                : Enumerable.Empty<Assembly>();
        }
    }

    /// <summary>
    /// Returns a non-generic WeakReference to the inner ALC for unload-
    /// verification in tests. Callers must null all strong refs and force GC
    /// after Dispose() to observe IsAlive → false.
    /// </summary>
    internal WeakReference GetAlcWeakReference() => new(_ctx);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void EnsureNotDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static void EnsureExecutableAssemblyArtifactsExist(string assemblyPath)
    {
        var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        var depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");

        if (File.Exists(runtimeConfigPath) && File.Exists(depsPath))
            return;

        var missingArtifacts = new List<string>();
        if (!File.Exists(runtimeConfigPath))
            missingArtifacts.Add(Path.GetFileName(runtimeConfigPath));
        if (!File.Exists(depsPath))
            missingArtifacts.Add(Path.GetFileName(depsPath));

        throw new InvalidOperationException(
            "QueryLens requires an executable assembly output as the target. " +
            $"Missing {string.Join(" and ", missingArtifacts)} next to '{Path.GetFileName(assemblyPath)}'. " +
            "Target the compiled API / Worker / Console assembly output, not a class library output.");
    }

    internal static bool ShouldPreferDefaultLoadContext(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return false;

        foreach (var prefix in SDefaultLoadContextPrefixes)
        {
            if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

}
