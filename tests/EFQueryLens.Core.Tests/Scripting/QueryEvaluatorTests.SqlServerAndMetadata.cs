using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;
using QueryEvaluator = EFQueryLens.Core.Scripting.Evaluation.QueryEvaluator;

namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    /// <summary>
    /// Regression coverage for the SQL Server sample when provider/runtime versions
    /// drift and <c>TranslateExecuteUpdate</c> is missing.
    ///
    /// Depending on runner environment and resolved package graph, this can either:
    /// 1) fail gracefully with a MissingMethodException-based message, or
    /// 2) succeed normally when versions are aligned.
    ///
    /// In both cases, the evaluator must not throw.
    /// </summary>
    [Fact]
    public async Task Evaluate_SqlServerSample_MissingMethodException_ReturnsGracefulFailureWithHint()
    {
        using var sqlAlcCtx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());
        var evaluator = new QueryEvaluator();

        var result = await evaluator.EvaluateAsync(
            sqlAlcCtx,
            new TranslationRequest
            {
                AssemblyPath = sqlAlcCtx.AssemblyPath,
                Expression = "db.Customers",
                DbContextTypeName = "SampleSqlServerApp.Infrastructure.Persistence.SqlServerAppDbContext",
            });

        if (result.Success)
        {
            // Aligned package/runtime graph: translation succeeds.
            Assert.NotNull(result.Sql);
            Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", result.Metadata.ProviderName);
            return;
        }

        // Drifted graph: must fail gracefully with actionable diagnostics.
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Method not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intra-project version conflict", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provider package", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_SqlServerSample_PagingWithExpressionVariable_SucceedsOrMethodNotFound()
    {
        // Regression: SqlClient runtime RID assets may leak internal metadata types
        // (for example SNIHandle) into Roslyn reference graphs. This must never surface
        // as CS0122/emit failure for the paging + Select(expression) hover scenario.
        //
        // Keep this aligned with the user-reported expression shape to catch regressions.
        using var sqlAlcCtx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());
        var evaluator = new QueryEvaluator();

        var result = await evaluator.EvaluateAsync(
            sqlAlcCtx,
            new TranslationRequest
            {
                AssemblyPath = sqlAlcCtx.AssemblyPath,
                Expression = "db.Orders.OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(expression)",
                DbContextTypeName = "SampleSqlServerApp.Infrastructure.Persistence.SqlServerAppDbContext",
                LocalVariableTypes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["page"] = "int",
                    ["pageSize"] = "int",
                    ["expression"] = "System.Linq.Expressions.Expression<System.Func<SampleSqlServerApp.Domain.Entities.Order, int>>",
                },
            });

        if (result.Success)
        {
            Assert.NotEmpty(result.Commands);
        }
        else
        {
            Assert.DoesNotContain("CS0122", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("SNIHandle", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Emit error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Method not found", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Verifies that the evaluator can resolve and use <c>SqlServerReportingDbContext</c>
    /// independently of <c>SqlServerAppDbContext</c> - the core multi-DbContext scenario.
    /// Uses the same bimodal assertion as the primary SqlServer test to tolerate provider drift.
    /// </summary>
    [Fact]
    public async Task Evaluate_SqlServerSample_ReportingContext_CustomerDirectory_ReturnsSql()
    {
        using var sqlAlcCtx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());
        var evaluator = new QueryEvaluator();

        var result = await evaluator.EvaluateAsync(
            sqlAlcCtx,
            new TranslationRequest
            {
                AssemblyPath = sqlAlcCtx.AssemblyPath,
                Expression = "db.CustomerDirectory",
                DbContextTypeName = "SampleSqlServerApp.Infrastructure.Persistence.SqlServerReportingDbContext",
            });

        if (result.Success)
        {
            // Aligned package/runtime graph: translation succeeds against the reporting context.
            Assert.NotNull(result.Sql);
            Assert.Contains("Customers", result.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", result.Metadata.ProviderName);
            return;
        }

        // Drifted graph: must fail gracefully - same diagnostics expected as primary context.
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Method not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MetaData_HasCorrectProviderName()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Pomelo.EntityFrameworkCore.MySql", result.Metadata.ProviderName);
    }

    [Fact]
    public async Task Evaluate_Metadata_TranslationTimeIsPositive()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Metadata.TranslationTime > TimeSpan.Zero);
    }

    [Fact]
    public async Task Evaluate_Metadata_DbContextTypeIsSet()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext", result.Metadata.DbContextType);
    }

    [Fact]
    public async Task Evaluate_Metadata_EfCoreVersionIsKnown()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEqual("unknown", result.Metadata.EfCoreVersion);
    }
}
