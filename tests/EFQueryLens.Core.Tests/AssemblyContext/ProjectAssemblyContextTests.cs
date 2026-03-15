using EFQueryLens.Core.AssemblyContext;

namespace EFQueryLens.Core.Tests.AssemblyContext;

/// <summary>
/// Unit tests for <see cref="ProjectAssemblyContext"/>.
///
/// SampleMySqlApp.dll is copied into an isolated SampleMySqlApp subfolder at build time
/// because EFQueryLens.Core.Tests.csproj references SampleMySqlApp with
/// ReferenceOutputAssembly="false". We locate it relative to this assembly's
/// location at runtime.
/// </summary>
[Collection("AssemblyLoadContextIsolation")]
public class ProjectAssemblyContextTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the absolute path to SampleMySqlApp.dll in the test output directory.
    /// </summary>
    private static string GetSampleMySqlAppDll()
    {
        var testDir = Path.GetDirectoryName(
            typeof(ProjectAssemblyContextTests).Assembly.Location)!;

        var dll = ResolveSampleDll(testDir, "SampleMySqlApp.dll");

        if (!File.Exists(dll))
            throw new FileNotFoundException(
                "SampleMySqlApp.dll not found in test output directory. " +
                "Make sure the solution is built before running tests. " +
                $"Expected: {dll}");

        return dll;
    }

    private static string ResolveSampleDll(string testOutputDir, string dllName)
    {
        var isolated = Path.Combine(testOutputDir, "SampleMySqlApp", dllName);
        if (File.Exists(isolated))
            return isolated;

        // Backward compatibility for older builds that copied files into root.
        return Path.Combine(testOutputDir, dllName);
    }

    private static string GetSampleSqlServerAppDll()
    {
        var testDir = Path.GetDirectoryName(
            typeof(ProjectAssemblyContextTests).Assembly.Location)!;

        var isolated = Path.Combine(testDir, "SampleSqlServerApp", "SampleSqlServerApp.dll");
        if (File.Exists(isolated))
            return isolated;

        return Path.Combine(testDir, "SampleSqlServerApp.dll");
    }

    // ─── Constructor / basic load ─────────────────────────────────────────────

    [Fact]
    public void Constructor_ValidAssembly_DoesNotThrow()
    {
        var dll = GetSampleMySqlAppDll();
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
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);
        Assert.NotEqual(default, ctx.AssemblyTimestamp);
    }

    [Theory]
    [InlineData("Microsoft.Build", true)]
    [InlineData("Microsoft.Build.Framework", true)]
    [InlineData("NuGet.ProjectModel", true)]
    [InlineData("Microsoft.VisualStudio.ProjectSystem", true)]
    [InlineData("Microsoft.CodeAnalysis.Workspaces.MSBuild", true)]
    [InlineData("Microsoft.TestPlatform.Utilities", true)]
    [InlineData("testhost", true)]
    [InlineData("Microsoft.Data.SqlClient", false)]
    [InlineData("Microsoft.EntityFrameworkCore", false)]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", false)]
    [InlineData("SampleMySqlApp", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void ShouldPreferDefaultLoadContext_ExpectedPolicy(string? assemblyName, bool expected)
    {
        var actual = ProjectAssemblyContext.ShouldPreferDefaultLoadContext(assemblyName);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryResolveRuntimeAssemblyPathFromReferencePath_MapsRefToLibWhenAvailable()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "querylens-ref-map-" + Guid.NewGuid().ToString("N"));
        try
        {
            var packageRoot = Path.Combine(tempRoot, "microsoft.data.sqlclient", "5.2.0");
            var refDir = Path.Combine(packageRoot, "ref", "net8.0");
            var libDir = Path.Combine(packageRoot, "lib", "net8.0");
            Directory.CreateDirectory(refDir);
            Directory.CreateDirectory(libDir);

            var refPath = Path.Combine(refDir, "Microsoft.Data.SqlClient.dll");
            var libPath = Path.Combine(libDir, "Microsoft.Data.SqlClient.dll");
            File.WriteAllText(refPath, "ref");
            File.WriteAllText(libPath, "runtime");

            var resolved = ProjectAssemblyContext.TryResolveRuntimeAssemblyPathFromReferencePath(
                refPath,
                "Microsoft.Data.SqlClient");

            Assert.Equal(libPath, resolved);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SqlServerSample_LoadsSqlClientFromRuntimeRidFolder()
    {
        using var ctx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());

        var sqlClientAssembly = ctx.LoadedAssemblies
            .FirstOrDefault(a => string.Equals(
                a.GetName().Name,
                "Microsoft.Data.SqlClient",
                StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(sqlClientAssembly);
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}runtimes{Path.DirectorySeparatorChar}",
            sqlClientAssembly!.Location,
            StringComparison.OrdinalIgnoreCase);
    }

    // ─── FindDbContextTypes ───────────────────────────────────────────────────

    [Fact]
    public void FindDbContextTypes_SampleMySqlApp_FindsExpectedContexts()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var types = ctx.FindDbContextTypes();

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.Name == "MySqlAppDbContext");
        Assert.Contains(types, t => t.Name == "MySqlReportingDbContext");
    }

    [Fact]
    public void FindDbContextTypes_SampleMySqlApp_FindsMySqlAppDbContext()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var types = ctx.FindDbContextTypes();

        Assert.Contains(types, t => t.Name == "MySqlAppDbContext");
    }

    [Fact]
    public void FindDbContextTypes_SampleMySqlApp_FullNameIsCorrect()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var types = ctx.FindDbContextTypes();

        Assert.Contains(types, t => t.FullName == "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext");
    }

    // ─── FindDbContextType ────────────────────────────────────────────────────

    [Fact]
    public void FindDbContextType_NullName_AutoDiscoversWhenOne()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var ex = Assert.Throws<InvalidOperationException>(() => ctx.FindDbContextType(null));

        Assert.Contains("Multiple DbContext types found", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MySqlAppDbContext", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MySqlReportingDbContext", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindDbContextType_SimpleName_Resolves()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var type = ctx.FindDbContextType("MySqlAppDbContext");

        Assert.Equal("SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext", type.FullName);
    }

    [Fact]
    public void FindDbContextType_FullyQualifiedName_Resolves()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var type = ctx.FindDbContextType("SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext");

        Assert.Equal("SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext", type.FullName);
    }

    [Fact]
    public void FindDbContextType_UnknownName_ThrowsInvalidOperationException()
    {
        var dll = GetSampleMySqlAppDll();
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
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var alcType = ctx.FindDbContextType("MySqlAppDbContext");

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
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var alcType    = ctx.FindDbContextType("MySqlAppDbContext");
        var typeAlc    = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(alcType.Assembly);
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
}
