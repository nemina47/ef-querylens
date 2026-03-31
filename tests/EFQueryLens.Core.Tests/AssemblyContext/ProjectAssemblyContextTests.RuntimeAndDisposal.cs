using EFQueryLens.Core.AssemblyContext;

namespace EFQueryLens.Core.Tests.AssemblyContext;

public partial class ProjectAssemblyContextTests
{
    // ─── ALC isolation ────────────────────────────────────────────────────────

    [Fact]
    public void IsolationTest_TypeFromALC_IsNotSameRuntimeTypeAsToolDbContext()
    {
        // Phase 2: EF Core is fully isolated — it is loaded from the user's bin dir
        // into the user's ALC. The tool itself has no compile-time EF Core dependency.
        //
        // Verify that:
        //   - AppDbContext lives in the user's ALC (not the default ALC).
        //   - The DbContext base class also lives in the user's ALC — confirming
        //     that EF Core loaded from the user's bin, not the tool's runtime.
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var alcType = ctx.FindDbContextType("MySqlAppDbContext");

        // AppDbContext must be in the user's ALC, not the default ALC.
        var userAlc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(alcType.Assembly);
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, userAlc);

        // DbContext base class must also be in the user's ALC — not the tool's ALC.
        var dbContextBase = alcType.BaseType;
        while (dbContextBase != null && dbContextBase != typeof(object)
               && dbContextBase.FullName != "Microsoft.EntityFrameworkCore.DbContext")
            dbContextBase = dbContextBase.BaseType;

        Assert.NotNull(dbContextBase);
        Assert.Equal("Microsoft.EntityFrameworkCore.DbContext", dbContextBase.FullName);

        var efAlc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(dbContextBase.Assembly);
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, efAlc);
    }

    [Fact]
    public void IsolationTest_AlcName_ContainsAssemblyName()
    {
        // Verify the ALC is distinctly named (aids debugging in memory dumps).
        // We access this indirectly: the type's Assembly's AssemblyLoadContext
        // should not be the default one.
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var alcType = ctx.FindDbContextType("MySqlAppDbContext");
        var typeAlc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(alcType.Assembly);
        var defaultAlc = System.Runtime.Loader.AssemblyLoadContext.Default;

        Assert.NotNull(typeAlc);
        Assert.NotSame(defaultAlc, typeAlc);
        Assert.Contains("SampleMySqlApp", typeAlc.Name);
    }

    // ─── Dispose / unload ─────────────────────────────────────────────────────

    [Fact]
    public void FindDbContextType_AfterDispose_ThrowsObjectDisposedException()
    {
        var dll = GetSampleMySqlAppDll();
        var ctx = new ProjectAssemblyContext(dll);
        ctx.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ctx.FindDbContextType());
    }

    [Fact]
    public void FindDbContextTypes_AfterDispose_ThrowsObjectDisposedException()
    {
        var dll = GetSampleMySqlAppDll();
        var ctx = new ProjectAssemblyContext(dll);
        ctx.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ctx.FindDbContextTypes());
    }

    [Fact]
    public void Dispose_SecondCall_IsIdempotent()
    {
        var dll = GetSampleMySqlAppDll();
        var ctx = new ProjectAssemblyContext(dll);
        ctx.Dispose();
        ctx.Dispose(); // must not throw
    }

    [Fact]
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Dispose_ThenGC_AlcIsCollected()
    {
        // This test must not inline so the local ProjectAssemblyContext has no
        // JIT-held stack roots keeping the ALC alive after Dispose().
        WeakReference alcRef = CreateAndDisposeContext();

        // Collectible ALC unload is eventually consistent with GC roots and JIT timing.
        // Assert eventual unload within a bounded window rather than a fixed iteration count.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (alcRef.IsAlive && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            if (alcRef.IsAlive)
            {
                System.Threading.Thread.Sleep(25);
            }
        }

        Assert.False(alcRef.IsAlive,
            "ALC should have been collected after Dispose() + GC. " +
            "Ensure no strong references remain.");
    }

    // Must be a separate non-inlined method so the JIT doesn't keep
    // the ProjectAssemblyContext (and thus the ALC) alive on the stack.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDisposeContext()
    {
        var dll = GetSampleMySqlAppDll();
        var ctx = new ProjectAssemblyContext(dll);
        var weakRef = ctx.GetAlcWeakReference();
        ctx.Dispose();
        return weakRef;
    }

    // ─── Factory ──────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_Create_ReturnsContextWithCorrectPath()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = ProjectAssemblyContextFactory.Create(dll);
        Assert.Equal(dll, ctx.AssemblyPath);
    }

    [Fact]
    public void Factory_IsStale_ReturnsFalseForFreshContext()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = ProjectAssemblyContextFactory.Create(dll);
        Assert.False(ProjectAssemblyContextFactory.IsStale(ctx));
    }

    // ─── LoadAdditionalAssembly / LoadedAssemblies post-dispose ───────────────

    [Fact]
    public void LoadedAssemblies_AfterDispose_ThrowsObjectDisposedException()
    {
        var dll = GetSampleMySqlAppDll();
        var ctx = new ProjectAssemblyContext(dll);
        ctx.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ctx.LoadedAssemblies.Any());
    }

    [Fact]
    public void LoadAdditionalAssembly_AfterDispose_ThrowsObjectDisposedException()
    {
        var dll = GetSampleMySqlAppDll();
        var ctx = new ProjectAssemblyContext(dll);
        ctx.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ctx.LoadAdditionalAssembly(dll));
    }

    [Fact]
    public void EnterContextualReflection_BeforeDispose_ReturnsDisposable()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        using var scope = ctx.EnterContextualReflection();
        Assert.NotNull(scope);
    }

    [Fact]
    public void LoadedAssemblies_BeforeDispose_ContainsMainAssembly()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        Assert.Contains(ctx.LoadedAssemblies,
            a => string.Equals(a.GetName().Name, "SampleMySqlApp", StringComparison.OrdinalIgnoreCase));
    }

    // ─── EnsureExecutableArtifacts — partial-missing branches ─────────────────

    [Fact]
    public void Constructor_MissingOnlyRuntimeConfig_ThrowsWithRuntimeConfigInMessage()
    {
        var sourceDll = GetSampleMySqlAppDll();
        var sourceDir = Path.GetDirectoryName(sourceDll)!;
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "querylens-only-deps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var dllName = Path.GetFileName(sourceDll);
            var baseName = Path.GetFileNameWithoutExtension(sourceDll);

            var destDll = Path.Combine(tempDir, dllName);
            File.Copy(sourceDll, destDll, overwrite: true);

            // Copy deps.json but NOT runtimeconfig.json.
            var srcDeps = Path.Combine(sourceDir, baseName + ".deps.json");
            if (File.Exists(srcDeps))
                File.Copy(srcDeps, Path.Combine(tempDir, baseName + ".deps.json"), overwrite: true);

            var ex = Assert.Throws<InvalidOperationException>(
                () => new ProjectAssemblyContext(destDll));
            Assert.Contains(".runtimeconfig.json", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Constructor_MissingOnlyDepsJson_ThrowsWithDepsInMessage()
    {
        var sourceDll = GetSampleMySqlAppDll();
        var sourceDir = Path.GetDirectoryName(sourceDll)!;
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "querylens-only-rtconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var dllName = Path.GetFileName(sourceDll);
            var baseName = Path.GetFileNameWithoutExtension(sourceDll);

            var destDll = Path.Combine(tempDir, dllName);
            File.Copy(sourceDll, destDll, overwrite: true);

            // Copy runtimeconfig.json but NOT deps.json.
            var srcRtConfig = Path.Combine(sourceDir, baseName + ".runtimeconfig.json");
            if (File.Exists(srcRtConfig))
                File.Copy(srcRtConfig, Path.Combine(tempDir, baseName + ".runtimeconfig.json"), overwrite: true);

            var ex = Assert.Throws<InvalidOperationException>(
                () => new ProjectAssemblyContext(destDll));
            Assert.Contains(".deps.json", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
