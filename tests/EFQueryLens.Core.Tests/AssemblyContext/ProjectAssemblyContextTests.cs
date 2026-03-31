using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Tests.AssemblyContext;

/// <summary>
/// Unit tests for <see cref="ProjectAssemblyContext"/>.
///
/// SampleMySqlApp.dll is copied into an isolated SampleMySqlApp subfolder at build time
/// because EFQueryLens.Core.Tests.csproj references SampleMySqlApp with
/// ReferenceOutputAssembly="false". We locate it relative to this assembly's
/// location at runtime.
/// </summary>
[Collection("ProjectAssemblyContextIsolation")]
public partial class ProjectAssemblyContextTests
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

    [Fact]
    public void Constructor_MissingExecutableArtifacts_ThrowsInvalidOperationException()
    {
        var sourceDll = GetSampleMySqlAppDll();
        var tempDir = Path.Combine(Path.GetTempPath(), "querylens-missing-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var copiedDll = Path.Combine(tempDir, Path.GetFileName(sourceDll));
            File.Copy(sourceDll, copiedDll, overwrite: true);

            var ex = Assert.Throws<InvalidOperationException>(() => new ProjectAssemblyContext(copiedDll));
            Assert.Contains("executable assembly output", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(".runtimeconfig.json", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(".deps.json", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
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
    public void FindDbContextTypes_SampleSqlServerApp_FindsBothDbContextTypes()
    {
        using var ctx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());

        var types = ctx.FindDbContextTypes();

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.Name == "SqlServerAppDbContext");
        Assert.Contains(types, t => t.Name == "SqlServerReportingDbContext");
    }

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

        var ex = Assert.Throws<DbContextDiscoveryException>(() => ctx.FindDbContextType(null));

        Assert.Equal(DbContextDiscoveryFailureKind.MultipleDbContextsFound, ex.FailureKind);
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
    public void FindDbContextType_InterfaceSimpleName_Resolves()
    {
        using var ctx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());

        var type = ctx.FindDbContextType("ISqlServerAppDbContext");

        Assert.Equal("SampleSqlServerApp.Infrastructure.Persistence.SqlServerAppDbContext", type.FullName);
    }

    [Fact]
    public void FindDbContextType_InterfaceFullyQualifiedName_Resolves()
    {
        using var ctx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());

        var type = ctx.FindDbContextType("SampleSqlServerApp.Application.Abstractions.ISqlServerAppDbContext");

        Assert.Equal("SampleSqlServerApp.Infrastructure.Persistence.SqlServerAppDbContext", type.FullName);
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

    [Fact]
    public void FindDbContextType_UnknownInterface_ThrowsInvalidOperationException()
    {
        using var ctx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());

        var ex = Assert.Throws<InvalidOperationException>(
            () => ctx.FindDbContextType("INoSuchDbContext"));

        Assert.Contains("INoSuchDbContext", ex.Message);
        Assert.Contains("Available", ex.Message);
    }

    [Fact]
    public void FindDbContextType_WithExpressionHint_DisambiguatesWithoutCrashing()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var type = ctx.FindDbContextType(null, "db.CustomerDirectory");

        Assert.Equal("SampleMySqlApp.Infrastructure.Persistence.MySqlReportingDbContext", type.FullName);
    }

    [Fact]
    public void FindDbContextType_WithMultipleFactoryCandidates_UsesExpressionHintToDisambiguate()
    {
        var dll = GetSampleMySqlAppDll();
        using var ctx = new ProjectAssemblyContext(dll);

        var type = ctx.FindDbContextType(
            expressionHint: "db.CustomerDirectory.Where(c => c.IsActive)",
            resolutionSnapshot: new DbContextResolutionSnapshot
            {
                FactoryCandidateTypeNames =
                [
                    "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext",
                    "SampleMySqlApp.Infrastructure.Persistence.MySqlReportingDbContext",
                ],
                ResolutionSource = "factory-candidates",
                Confidence = 0.5,
            });

        Assert.Equal("SampleMySqlApp.Infrastructure.Persistence.MySqlReportingDbContext", type.FullName);
    }

    [Fact]
    public void FindDbContextType_WhenRequestedContextConflictsWithExpressionRoot_ThrowsTypedException()
    {
        using var ctx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());

        var ex = Assert.Throws<DbContextDiscoveryException>(() =>
            ctx.FindDbContextType(
                "SampleSqlServerApp.Infrastructure.Persistence.SqlServerAppDbContext",
                "db.CustomerDirectory"));

        Assert.Equal(DbContextDiscoveryFailureKind.ConflictingDbContextHints, ex.FailureKind);
        Assert.Contains("CustomerDirectory", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SqlServerReportingDbContext", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindDbContextType_WhenDeclaredAndFactoryHintsConflict_ThrowsTypedException()
    {
        using var ctx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());

        var ex = Assert.Throws<DbContextDiscoveryException>(() =>
            ctx.FindDbContextType(
                expressionHint: null,
                resolutionSnapshot: new DbContextResolutionSnapshot
                {
                    DeclaredTypeName = "SampleSqlServerApp.Infrastructure.Persistence.SqlServerAppDbContext",
                    FactoryTypeName = "SampleSqlServerApp.Infrastructure.Persistence.SqlServerReportingDbContext",
                    FactoryCandidateTypeNames =
                    [
                        "SampleSqlServerApp.Infrastructure.Persistence.SqlServerAppDbContext",
                        "SampleSqlServerApp.Infrastructure.Persistence.SqlServerReportingDbContext",
                    ],
                    ResolutionSource = "declared+factory-mismatch",
                    Confidence = 0.4,
                }));

        Assert.Equal(DbContextDiscoveryFailureKind.ConflictingDbContextHints, ex.FailureKind);
        Assert.Contains("declared", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("factory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

}
