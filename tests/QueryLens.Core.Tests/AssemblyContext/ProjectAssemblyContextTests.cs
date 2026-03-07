using QueryLens.Core.AssemblyContext;

namespace QueryLens.Core.Tests.AssemblyContext;

/// <summary>
/// Unit tests for <see cref="ProjectAssemblyContext"/>.
///
/// SampleApp.dll lands next to the test binary at build time because
/// QueryLens.Core.Tests.csproj references SampleApp with
/// ReferenceOutputAssembly="false". We locate it relative to this assembly's
/// location at runtime.
/// </summary>
[Collection("AssemblyLoadContextIsolation")]
public class ProjectAssemblyContextTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the absolute path to SampleApp.dll in the test output directory.
    /// </summary>
    private static string GetSampleAppDll()
    {
        var testDir = Path.GetDirectoryName(
            typeof(ProjectAssemblyContextTests).Assembly.Location)!;

        var dll = Path.Combine(testDir, "SampleApp.dll");

        if (!File.Exists(dll))
            throw new FileNotFoundException(
                "SampleApp.dll not found in test output directory. " +
                "Make sure the solution is built before running tests. " +
                $"Expected: {dll}");

        return dll;
    }

    // ─── Constructor / basic load ─────────────────────────────────────────────

    [Fact]
    public void Constructor_ValidAssembly_DoesNotThrow()
    {
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);
        Assert.Equal(dll, ctx.AssemblyPath);
    }

    [Fact]
    public void Constructor_MissingFile_ThrowsFileNotFoundException()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "DoesNotExist.dll");
        Assert.Throws<FileNotFoundException>(() => new ProjectAssemblyContext(bogus));
    }

    [Fact]
    public void Constructor_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new ProjectAssemblyContext(""));
    }

    [Fact]
    public void AssemblyTimestamp_IsSet()
    {
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);
        Assert.NotEqual(default, ctx.AssemblyTimestamp);
    }

    // ─── FindDbContextTypes ───────────────────────────────────────────────────

    [Fact]
    public void FindDbContextTypes_SampleApp_FindsExactlyOneContext()
    {
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var types = ctx.FindDbContextTypes();

        Assert.Single(types);
    }

    [Fact]
    public void FindDbContextTypes_SampleApp_FindsAppDbContext()
    {
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var types = ctx.FindDbContextTypes();

        Assert.Contains(types, t => t.Name == "AppDbContext");
    }

    [Fact]
    public void FindDbContextTypes_SampleApp_FullNameIsCorrect()
    {
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var types = ctx.FindDbContextTypes();

        Assert.Contains(types, t => t.FullName == "SampleApp.AppDbContext");
    }

    // ─── FindDbContextType ────────────────────────────────────────────────────

    [Fact]
    public void FindDbContextType_NullName_AutoDiscoversWhenOne()
    {
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var type = ctx.FindDbContextType(null);

        Assert.Equal("SampleApp.AppDbContext", type.FullName);
    }

    [Fact]
    public void FindDbContextType_SimpleName_Resolves()
    {
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var type = ctx.FindDbContextType("AppDbContext");

        Assert.Equal("SampleApp.AppDbContext", type.FullName);
    }

    [Fact]
    public void FindDbContextType_FullyQualifiedName_Resolves()
    {
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var type = ctx.FindDbContextType("SampleApp.AppDbContext");

        Assert.Equal("SampleApp.AppDbContext", type.FullName);
    }

    [Fact]
    public void FindDbContextType_UnknownName_ThrowsInvalidOperationException()
    {
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ctx.FindDbContextType("NoSuchContext"));

        Assert.Contains("NoSuchContext", ex.Message);
        Assert.Contains("Available", ex.Message);
    }

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
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var alcType = ctx.FindDbContextType("AppDbContext");

        // AppDbContext must be in the user's ALC, not the default ALC.
        var userAlc  = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(alcType.Assembly);
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
        var dll = GetSampleAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var alcType    = ctx.FindDbContextType();
        var typeAlc    = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(alcType.Assembly);
        var defaultAlc = System.Runtime.Loader.AssemblyLoadContext.Default;

        Assert.NotNull(typeAlc);
        Assert.NotSame(defaultAlc, typeAlc);
        Assert.Contains("SampleApp", typeAlc.Name);
    }

    // ─── Dispose / unload ─────────────────────────────────────────────────────

    [Fact]
    public void FindDbContextType_AfterDispose_ThrowsObjectDisposedException()
    {
        var dll = GetSampleAppDll();
        var ctx = new ProjectAssemblyContext(dll);
        ctx.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ctx.FindDbContextType());
    }

    [Fact]
    public void FindDbContextTypes_AfterDispose_ThrowsObjectDisposedException()
    {
        var dll = GetSampleAppDll();
        var ctx = new ProjectAssemblyContext(dll);
        ctx.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ctx.FindDbContextTypes());
    }

    [Fact]
    public void Dispose_SecondCall_IsIdempotent()
    {
        var dll = GetSampleAppDll();
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
        var dll = GetSampleAppDll();
        var ctx = new ProjectAssemblyContext(dll);
        var weakRef = ctx.GetAlcWeakReference();
        ctx.Dispose();
        return weakRef;
    }

    // ─── Factory ──────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_Create_ReturnsContextWithCorrectPath()
    {
        var dll = GetSampleAppDll();
        using var ctx = ProjectAssemblyContextFactory.Create(dll);
        Assert.Equal(dll, ctx.AssemblyPath);
    }

    [Fact]
    public void Factory_IsStale_ReturnsFalseForFreshContext()
    {
        var dll = GetSampleAppDll();
        using var ctx = ProjectAssemblyContextFactory.Create(dll);
        Assert.False(ProjectAssemblyContextFactory.IsStale(ctx));
    }
}
