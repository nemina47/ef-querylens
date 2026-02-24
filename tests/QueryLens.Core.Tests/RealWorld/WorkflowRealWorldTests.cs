using QueryLens.Core;
using QueryLens.Core.AssemblyContext;
using QueryLens.Core.Scripting;
using QueryLens.MySql;

namespace QueryLens.Core.Tests.RealWorld;

/// <summary>
/// Real-world validation tests against the share-common-workflow project.
///
/// These tests are skipped automatically when the project's built output is
/// not present, so they are safe to run on any machine (CI included).
///
/// To run them locally:
///   1. Build share-common-workflow in Debug mode (net8.0)
///   2. Run: dotnet test --filter "RealWorld"
/// </summary>
public class WorkflowRealWorldTests
{
    private const string BinDir =
        @"C:\nemina\QueryLens\samples\share-common-workflow\src\Share.Common.Workflow.Api\bin\Debug\net8.0";

    private static readonly string ApiDll  = Path.Combine(BinDir, "Share.Common.Workflow.Api.dll");
    private static readonly string CoreDll = Path.Combine(BinDir, "Share.Common.Workflow.Core.dll");

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static void SkipIfNotPresent()
    {
        Skip.IfNot(
            Directory.Exists(BinDir) && File.Exists(ApiDll) && File.Exists(CoreDll),
            "share-common-workflow build output not present — skipping real-world test.");
    }

    private static (QueryEvaluator Evaluator, IProviderBootstrap Bootstrap) CreateEvaluator() =>
        (new QueryEvaluator(), new MySqlProviderBootstrap());

    private static ProjectAssemblyContext CreateContext()
    {
        // Load the API dll as primary so its deps.json resolves all transitive
        // dependencies (Share.Lib.*, Audit.EntityFramework, etc.).
        // Then explicitly pre-load the Core class library so FindDbContextTypes()
        // can find WorkflowDbContext (which lives in Core, not the API assembly).
        var ctx = new ProjectAssemblyContext(ApiDll);
        ctx.LoadAdditionalAssembly(CoreDll);
        return ctx;
    }

    private static TranslationRequest BuildRequest(string expression) =>
        new()
        {
            AssemblyPath        = ApiDll,
            DbContextTypeName   = "WorkflowDbContext",
            ContextVariableName = "db",
            Expression          = expression,
        };

    // ─── Tests ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Workflow_SimpleTableScan_GeneratesSql()
    {
        SkipIfNotPresent();

        using var ctx = CreateContext();
        var (eval, bootstrap) = CreateEvaluator();

        var result = await eval.EvaluateAsync(ctx, bootstrap,
            BuildRequest("db.AppWorkflowLevelStageActionRemarks"));

        Assert.True(result.Success, $"Error: {result.ErrorMessage}");
        Assert.NotNull(result.Sql);
        Assert.Contains("AppWorkflowLevelStageActionRemarks", result.Sql, StringComparison.OrdinalIgnoreCase);

        // Report for inspection
        Console.WriteLine($"[CreationStrategy] {result.Metadata.CreationStrategy}");
        Console.WriteLine($"[EF Core version]  {result.Metadata.EfCoreVersion}");
        Console.WriteLine($"[SQL]\n{result.Sql}");
    }

    [SkippableFact]
    public async Task Workflow_AsNoTracking_WhereIsNotDeleted_GeneratesSql()
    {
        SkipIfNotPresent();

        using var ctx = CreateContext();
        var (eval, bootstrap) = CreateEvaluator();

        // IsNotDeleted is a computed C# property (=> !IsDeleted) from AuditableEntity —
        // EF Core cannot translate unmapped computed properties to SQL. Use the equivalent
        // translatable form: !w.IsDeleted (IsDeleted IS a mapped DB column).
        var result = await eval.EvaluateAsync(ctx, bootstrap,
            BuildRequest(
                "db.AppWorkflowLevelStageActionRemarks" +
                ".AsNoTracking()" +
                ".Where(w => !w.IsDeleted)"));

        Assert.True(result.Success, $"Error: {result.ErrorMessage}");
        Assert.NotNull(result.Sql);
        Assert.Contains("AppWorkflowLevelStageActionRemarks", result.Sql, StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"[SQL]\n{result.Sql}");
    }

    [SkippableFact]
    public async Task Workflow_WhereApplicationId_WithLocalVar_GeneratesSql()
    {
        SkipIfNotPresent();

        using var ctx = CreateContext();
        var (eval, bootstrap) = CreateEvaluator();

        // Declare applicationId as a local variable in the script so the
        // LINQ closure can capture it (mirrors the user's production expression).
        // IsNotDeleted is a computed C# property (=> !IsDeleted) from AuditableEntity —
        // EF Core cannot translate unmapped computed properties. Use !w.IsDeleted instead.
        var result = await eval.EvaluateAsync(ctx, bootstrap,
            BuildRequest(
                "var applicationId = Guid.Empty; " +
                "db.AppWorkflowLevelStageActionRemarks" +
                ".AsNoTracking()" +
                ".Where(w => !w.IsDeleted)" +
                ".Where(w => w.ApplicationId == applicationId)"));

        Assert.True(result.Success, $"Error: {result.ErrorMessage}");
        Assert.NotNull(result.Sql);
        Assert.Contains("WHERE", result.Sql, StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"[Parameters] {result.Parameters.Count}");
        Console.WriteLine($"[SQL]\n{result.Sql}");
    }

    [SkippableFact]
    public async Task Workflow_DbContextType_IsDiscoveredCorrectly()
    {
        SkipIfNotPresent();

        // Verify that FindDbContextTypes() finds WorkflowDbContext in Core.dll
        // even though the primary assembly loaded is the API dll.
        using var ctx = CreateContext();

        var types = ctx.FindDbContextTypes();

        Assert.Contains(types, t => t.Name == "WorkflowDbContext");
        Console.WriteLine($"[DbContext types found] {string.Join(", ", types.Select(t => t.FullName))}");
    }

    [SkippableFact]
    public async Task Workflow_MetadataCreationStrategy_IsBootstrap()
    {
        SkipIfNotPresent();

        // No IDesignTimeDbContextFactory in this project → expect bootstrap path.
        using var ctx = CreateContext();
        var (eval, bootstrap) = CreateEvaluator();

        var result = await eval.EvaluateAsync(ctx, bootstrap,
            BuildRequest("db.AppWorkflows"));

        Assert.True(result.Success, $"Error: {result.ErrorMessage}");
        Assert.Equal("bootstrap", result.Metadata.CreationStrategy);

        Console.WriteLine($"[SQL]\n{result.Sql}");
    }
}
