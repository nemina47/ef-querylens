using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public class MethodQueryInlinerTests
{
    [Fact]
    public void TryInlineTopLevelInvocation_ReturnAwaitMethod_InlinesQueryBody()
    {
        var workspaceRoot = CreateWorkspace();

        try
        {
            var apiFile = Path.Combine(workspaceRoot, "Api", "GetWorkflowByType.cs");
            var coreFile = Path.Combine(workspaceRoot, "Core", "WorkflowService.cs");

            Directory.CreateDirectory(Path.GetDirectoryName(apiFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(coreFile)!);
            File.WriteAllText(Path.Combine(workspaceRoot, "Sample.sln"), "");

            File.WriteAllText(coreFile, """
                using System.Linq.Expressions;
                using System.Threading;
                using System.Threading.Tasks;

                public sealed class WorkflowService
                {
                    public async Task<TResult?> GetWorkflowByTypeAsync<TResult>(
                        WorkflowType workflowType,
                        Expression<Func<Workflow, TResult>> expression,
                        CancellationToken ct)
                    {
                        return await dbContext
                            .Workflows.Where(w => w.WorkflowType == workflowType && w.IsNotDeleted)
                            .Select(expression)
                            .SingleOrDefaultAsync(ct);
                    }
                }
                """);

            var endpointSource = """
                var data = await service.GetWorkflowByTypeAsync(
                    req.WorkflowType,
                    w => new WorkflowResponse { WorkflowType = w.WorkflowType },
                    ct);
                """;
            File.WriteAllText(apiFile, endpointSource);

            var expression = "service.GetWorkflowByTypeAsync(req.WorkflowType, w => new WorkflowResponse { WorkflowType = w.WorkflowType }, ct)";

            var success = MethodQueryInliner.TryInlineTopLevelInvocation(
                endpointSource,
                apiFile,
                expression,
                substituteSelectorArguments: false,
                out var inlined,
                out var contextVariable,
                out var selectedMethodSourcePath,
                out var reason);

            Assert.True(success, reason);
            Assert.Equal("dbContext", contextVariable);
            Assert.Contains("dbContext.Workflows", inlined, StringComparison.Ordinal);
            Assert.Contains("w.WorkflowType == workflowType", inlined, StringComparison.Ordinal);
            Assert.Contains("Select(expression)", inlined, StringComparison.Ordinal);
            Assert.DoesNotContain("SingleOrDefaultAsync", inlined, StringComparison.Ordinal);
            Assert.Equal(coreFile, selectedMethodSourcePath);
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public void TryInlineTopLevelInvocation_ExpressionBodiedMethod_InlinesQueryBody()
    {
        var workspaceRoot = CreateWorkspace();

        try
        {
            var apiFile = Path.Combine(workspaceRoot, "Api", "CreateApplication.cs");
            var coreFile = Path.Combine(workspaceRoot, "Core", "WorkflowService.cs");

            Directory.CreateDirectory(Path.GetDirectoryName(apiFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(coreFile)!);
            File.WriteAllText(Path.Combine(workspaceRoot, "Sample.sln"), "");

            File.WriteAllText(coreFile, """
                using System.Linq.Expressions;
                using System.Threading;
                using System.Threading.Tasks;

                public sealed class WorkflowService
                {
                    public Task<TResult?> GetWorkflowByIdAsync<TResult>(
                        Guid workflowId,
                        Expression<Func<Workflow, TResult>> expression,
                        CancellationToken ct)
                        => dbContext.Workflows
                            .Where(w => w.WorkflowId == workflowId)
                            .Select(expression)
                            .SingleOrDefaultAsync(ct);
                }
                """);

            var endpointSource = """
                var workflowId = await service.GetWorkflowByIdAsync(
                    req.WorkflowId,
                    w => w.WorkflowId,
                    ct);
                """;
            File.WriteAllText(apiFile, endpointSource);

            var expression = "service.GetWorkflowByIdAsync(req.WorkflowId, w => w.WorkflowId, ct)";

            var success = MethodQueryInliner.TryInlineTopLevelInvocation(
                endpointSource,
                apiFile,
                expression,
                substituteSelectorArguments: false,
                out var inlined,
                out var contextVariable,
                out var reason);

            Assert.True(success, reason);
            Assert.Equal("dbContext", contextVariable);
            Assert.Contains("dbContext.Workflows", inlined, StringComparison.Ordinal);
            Assert.Contains("w.WorkflowId == workflowId", inlined, StringComparison.Ordinal);
            Assert.Contains("Select(expression)", inlined, StringComparison.Ordinal);
            Assert.DoesNotContain("SingleOrDefaultAsync", inlined, StringComparison.Ordinal);
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public void TryInlineTopLevelInvocation_UnknownMethod_ReturnsFalse()
    {
        var workspaceRoot = CreateWorkspace();

        try
        {
            var apiFile = Path.Combine(workspaceRoot, "Api", "GetWorkflowByType.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(apiFile)!);
            File.WriteAllText(Path.Combine(workspaceRoot, "Sample.sln"), "");

            var source = """
                var data = await service.DoesNotExist(req.WorkflowType, w => w.WorkflowType, ct);
                """;
            File.WriteAllText(apiFile, source);

            var expression = "service.DoesNotExist(req.WorkflowType, w => w.WorkflowType, ct)";

            var success = MethodQueryInliner.TryInlineTopLevelInvocation(
                source,
                apiFile,
                expression,
                substituteSelectorArguments: false,
                out var inlined,
                out var contextVariable,
                out var reason);

            Assert.False(success);
            Assert.Equal(expression, inlined);
            Assert.Null(contextVariable);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public void TryInlineTopLevelInvocation_SelectorSubstitutionEnabled_InlinesConcreteProjection()
    {
        var workspaceRoot = CreateWorkspace();

        try
        {
            var apiFile = Path.Combine(workspaceRoot, "Api", "GetWorkflowByType.cs");
            var coreFile = Path.Combine(workspaceRoot, "Core", "WorkflowService.cs");

            Directory.CreateDirectory(Path.GetDirectoryName(apiFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(coreFile)!);
            File.WriteAllText(Path.Combine(workspaceRoot, "Sample.sln"), "");

            File.WriteAllText(coreFile, """
                using System.Linq.Expressions;
                using System.Threading;
                using System.Threading.Tasks;

                public sealed class WorkflowService
                {
                    public async Task<TResult?> GetWorkflowByTypeAsync<TResult>(
                        WorkflowType workflowType,
                        Expression<Func<Workflow, TResult>> expression,
                        CancellationToken ct)
                    {
                        return await dbContext
                            .Workflows.Where(w => w.WorkflowType == workflowType && w.IsNotDeleted)
                            .Select(expression)
                            .SingleOrDefaultAsync(ct);
                    }
                }
                """);

            var endpointSource = """
                var data = await service.GetWorkflowByTypeAsync(
                    req.WorkflowType,
                    w => new WorkflowResponse { WorkflowType = w.WorkflowType },
                    ct);
                """;
            File.WriteAllText(apiFile, endpointSource);

            var expression = "service.GetWorkflowByTypeAsync(req.WorkflowType, w => new WorkflowResponse { WorkflowType = w.WorkflowType }, ct)";

            var success = MethodQueryInliner.TryInlineTopLevelInvocation(
                endpointSource,
                apiFile,
                expression,
                substituteSelectorArguments: true,
                out var inlined,
                out var contextVariable,
                out var reason);

            Assert.True(success, reason);
            Assert.Equal("dbContext", contextVariable);
            Assert.Contains("dbContext.Workflows", inlined, StringComparison.Ordinal);
            Assert.Contains("w.WorkflowType == workflowType", inlined, StringComparison.Ordinal);
            Assert.Contains("Select(w => new { WorkflowType = w.WorkflowType })", inlined, StringComparison.Ordinal);
            Assert.DoesNotContain("SingleOrDefaultAsync", inlined, StringComparison.Ordinal);
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public void TryInlineTopLevelInvocation_SelectorWithNestedToList_ReportsMaterializationRisk()
    {
        var workspaceRoot = CreateWorkspace();

        try
        {
            var apiFile = Path.Combine(workspaceRoot, "Api", "GetApplication.cs");
            var coreFile = Path.Combine(workspaceRoot, "Core", "ApplicationService.cs");

            Directory.CreateDirectory(Path.GetDirectoryName(apiFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(coreFile)!);
            File.WriteAllText(Path.Combine(workspaceRoot, "Sample.sln"), "");

            File.WriteAllText(coreFile, """
                using System.Linq.Expressions;
                using System.Threading;
                using System.Threading.Tasks;

                public sealed class ApplicationService
                {
                    public async Task<TResult?> GetByIdAsync<TResult>(
                        Guid applicationId,
                        Expression<Func<Application, TResult>> expression,
                        CancellationToken ct)
                    {
                        return await dbContext.Applications
                            .Where(a => a.ApplicationId == applicationId)
                            .Select(expression)
                            .SingleOrDefaultAsync(ct);
                    }
                }
                """);

            var endpointSource = """
                var data = await service.GetByIdAsync(
                    applicationId,
                    app => new ApplicationResponse
                    {
                        ChangeTypes = app.ChangeTypes
                            .Where(t => !t.IsDeleted)
                            .Select(t => t.Name)
                            .ToList()
                    },
                    ct);
                """;
            File.WriteAllText(apiFile, endpointSource);

            var expression = "service.GetByIdAsync(applicationId, app => new ApplicationResponse { ChangeTypes = app.ChangeTypes.Where(t => !t.IsDeleted).Select(t => t.Name).ToList() }, ct)";

            var success = MethodQueryInliner.TryInlineTopLevelInvocation(
                endpointSource,
                apiFile,
                expression,
                substituteSelectorArguments: true,
                out var inlined,
                out var contextVariable,
                out var selectedMethodSourcePath,
                out var diagnostics,
                out var reason);

            Assert.True(success, reason);
            Assert.Equal("dbContext", contextVariable);
            Assert.Equal(coreFile, selectedMethodSourcePath);
            Assert.Contains("Select(app => new", inlined, StringComparison.Ordinal);
            Assert.True(diagnostics.SelectorParameterDetected);
            Assert.True(diagnostics.SelectorArgumentSubstituted);
            Assert.True(diagnostics.SelectorArgumentSanitized);
            Assert.True(diagnostics.ContainsNestedSelectorMaterialization);
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public void TryInlineTopLevelInvocation_AsyncPagedWrapperMethod_InlinesItemsQuery()
    {
        var workspaceRoot = CreateWorkspace();

        try
        {
            var apiFile = Path.Combine(workspaceRoot, "Api", "OrdersEndpoint.cs");
            var serviceFile = Path.Combine(workspaceRoot, "Core", "OrderService.cs");

            Directory.CreateDirectory(Path.GetDirectoryName(apiFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(serviceFile)!);
            File.WriteAllText(Path.Combine(workspaceRoot, "Sample.sln"), "");

            File.WriteAllText(serviceFile, """
                using System;
                using System.Linq.Expressions;
                using System.Threading;
                using System.Threading.Tasks;

                public sealed class OrderService
                {
                    public async Task<PagedResult<TResult>> GetPagedOrdersAsync<TResult>(
                        OrderQueryRequest request,
                        Expression<Func<Order, TResult>> expression,
                        CancellationToken ct)
                    {
                        var baseQuery = dbContext.Orders.Where(o => o.IsNotDeleted);

                        var totalCount = await baseQuery.CountAsync(ct);

                        var items = await baseQuery
                            .OrderByDescending(o => o.CreatedUtc)
                            .ThenByDescending(o => o.Id)
                            .Skip((request.Page - 1) * request.PageSize)
                            .Take(request.PageSize)
                            .Select(expression)
                            .ToListAsync(ct);

                        return new PagedResult<TResult>(items, totalCount, request.Page, request.PageSize);
                    }
                }
                """);

            var endpointSource = """
                var result = await service.GetPagedOrdersAsync(
                    request,
                    o => new OrderListItemResponse { OrderId = o.Id, Total = o.Total },
                    ct);
                """;
            File.WriteAllText(apiFile, endpointSource);

            var expression = "service.GetPagedOrdersAsync(request, o => new OrderListItemResponse { OrderId = o.Id, Total = o.Total }, ct)";

            var success = MethodQueryInliner.TryInlineTopLevelInvocation(
                endpointSource,
                apiFile,
                expression,
                substituteSelectorArguments: false,
                out var inlined,
                out var contextVariable,
                out var selectedMethodSourcePath,
                out var reason);

            Assert.True(success, reason);
            Assert.Equal("dbContext", contextVariable);
            Assert.Equal(serviceFile, selectedMethodSourcePath);
            Assert.Contains("dbContext.Orders", inlined, StringComparison.Ordinal);
            Assert.Contains("Skip((request.Page - 1) * request.PageSize)", inlined, StringComparison.Ordinal);
            Assert.Contains("Select(expression)", inlined, StringComparison.Ordinal);
            Assert.DoesNotContain("CountAsync", inlined, StringComparison.Ordinal);
            Assert.DoesNotContain("ToListAsync", inlined, StringComparison.Ordinal);
        }
        finally
        {
            DeleteWorkspace(workspaceRoot);
        }
    }

    [Fact]
    public void TryInlineTopLevelInvocation_SampleMySqlAppScenario_ReportsMaterializationRisk()
    {
        var endpointFile = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "samples", "SampleMySqlApp", "QueryScenarios", "ApplicationChecklistEndpointSamples.cs"));

        var serviceFile = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "samples", "SampleMySqlApp", "QueryScenarios", "ApplicationChecklistScenarioService.cs"));

        Assert.True(File.Exists(endpointFile), $"Sample endpoint file not found: {endpointFile}");
        Assert.True(File.Exists(serviceFile), $"Sample service file not found: {serviceFile}");

        var source = File.ReadAllText(endpointFile);
        var expression = "service.GetChecklistByApplicationIdAsync(applicationId, checklist => new ApplicationChecklistResponse { ApplicationId = checklist.ApplicationId, ChangeTypes = checklist.ChecklistChangeTypes.Where(t => !t.IsDeleted).Select(t => t.ChangeType).ToList() }, ct)";

        var success = MethodQueryInliner.TryInlineTopLevelInvocation(
            source,
            endpointFile,
            expression,
            substituteSelectorArguments: true,
            out var inlined,
            out var contextVariable,
            out var selectedMethodSourcePath,
            out var diagnostics,
            out var reason);

        Assert.True(success, reason);
        Assert.Equal("dbContext", contextVariable);
        Assert.Equal(serviceFile, selectedMethodSourcePath);
        Assert.Contains("Select(checklist => new", inlined, StringComparison.Ordinal);
        Assert.True(diagnostics.SelectorParameterDetected);
        Assert.True(diagnostics.SelectorArgumentSubstituted);
        Assert.True(diagnostics.SelectorArgumentSanitized);
        Assert.True(diagnostics.ContainsNestedSelectorMaterialization);
    }

    [Fact]
    public void TryInlineTopLevelInvocation_SampleMySqlAppPagedOrdersEndpoint_InlinesInternalQuery()
    {
        var endpointFile = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "samples", "SampleMySqlApp", "Program.cs"));

        var serviceFile = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "samples", "SampleMySqlApp", "Application", "Customers", "CustomerReadService.cs"));

        Assert.True(File.Exists(endpointFile), $"Sample endpoint file not found: {endpointFile}");
        Assert.True(File.Exists(serviceFile), $"Sample service file not found: {serviceFile}");

        var source = File.ReadAllText(endpointFile);
        var expression = "service.GetPagedOrdersAsync(request, o => new OrderListItemResponse { OrderId = o.Id, CustomerId = o.Customer.CustomerId, Total = o.Total, Status = o.Status, CreatedUtc = o.CreatedUtc }, ct)";

        var success = MethodQueryInliner.TryInlineTopLevelInvocation(
            source,
            endpointFile,
            expression,
            substituteSelectorArguments: false,
            out var inlined,
            out var contextVariable,
            out var selectedMethodSourcePath,
            out var reason);

        Assert.True(success, reason);
        Assert.Equal("_dbContext", contextVariable);
        Assert.Equal(serviceFile, selectedMethodSourcePath);
        Assert.Contains("_dbContext.Orders", inlined, StringComparison.Ordinal);
        Assert.Contains("Select(expression)", inlined, StringComparison.Ordinal);
        Assert.DoesNotContain("service.GetPagedOrdersAsync", inlined, StringComparison.Ordinal);
    }

    [Fact]
    public void TryInlineTopLevelInvocation_SampleMySqlAppRecentOrdersBuilderCall_InlinesAndPreservesSelect()
    {
        var endpointFile = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "samples", "SampleMySqlApp", "Program.cs"));

        var queryFile = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "samples", "SampleMySqlApp", "Application", "Orders", "OrderQueries.cs"));

        Assert.True(File.Exists(endpointFile), $"Sample endpoint file not found: {endpointFile}");
        Assert.True(File.Exists(queryFile), $"Sample query file not found: {queryFile}");

        var source = File.ReadAllText(endpointFile);
        var expression = "orderQueries.BuildRecentOrdersQuery(DateTime.UtcNow, lookbackDays).Select(o => new RecentOrderResponse { OrderId = o.OrderId, CustomerName = o.CustomerName, Total = o.Total, CreatedUtc = o.CreatedUtc })";

        var success = MethodQueryInliner.TryInlineTopLevelInvocation(
            source,
            endpointFile,
            expression,
            substituteSelectorArguments: false,
            out var inlined,
            out var contextVariable,
            out var selectedMethodSourcePath,
            out var reason);

        Assert.True(success, reason);
        Assert.Equal("_db", contextVariable);
        Assert.Equal(queryFile, selectedMethodSourcePath);
        Assert.Contains("_db.Orders", inlined, StringComparison.Ordinal);
        Assert.Contains("Select(o => new RecentOrderResponse", inlined, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildRecentOrdersQuery", inlined, StringComparison.Ordinal);
    }

    private static string CreateWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "querylens-inliner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteWorkspace(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}