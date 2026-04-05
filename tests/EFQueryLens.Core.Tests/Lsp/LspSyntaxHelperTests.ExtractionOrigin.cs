using EFQueryLens.Lsp.Parsing;
using System.IO;

namespace EFQueryLens.Core.Tests.Lsp;

public partial class LspSyntaxHelperTests
{
    [Fact]
    public void TryExtractLinqExpressionDetailed_HelperQueryableCall_UsesHelperOrigin()
    {
        var source = """
            class CustomerReadService
            {
                public List<int> GetIds(Guid customerId)
                {
                    return BuildRecentOrdersQuery(customerId).Select(o => o.Id).ToList();
                }

                IQueryable<Order> BuildRecentOrdersQuery(Guid id)
                {
                    var page = 2;
                    return _dbContext.Orders
                        .Where(o => o.Customer.CustomerId == id)
                        .Skip((page - 1) * 10)
                        .Take(10);
                }
            }
            """;

        var (line, character) = FindPosition(source, "BuildRecentOrdersQuery(customerId).Select");
        var result = LspSyntaxHelper.TryExtractLinqExpressionDetailed(
            source,
            filePath: @"c:\repo\CustomerReadService.cs",
            line,
            character);

        Assert.NotNull(result);
        Assert.Equal("_dbContext", result.ContextVariableName);
        Assert.Equal("helper-method", result.Origin.Scope);
        Assert.NotEqual(line, result.Origin.Line);
        Assert.Contains("_dbContext.Orders", result.Expression, StringComparison.Ordinal);
        Assert.Contains(".Where(o => o.Customer.CustomerId == customerId)", result.Expression, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLinqExpressionDetailed_DoesNotInjectSemanticCommentHints()
    {
        var source = """
            class Demo
            {
                void M(Guid customerId)
                {
                    var q = _dbContext.Orders
                        .Where(o => o.Customer.CustomerId == customerId)
                        .Where(o => o.Customer.CustomerId == customerId);
                }
            }
            """;

        var (line, character) = FindPosition(source, ".Where(o => o.Customer.CustomerId == customerId);");
        var result = LspSyntaxHelper.TryExtractLinqExpressionDetailed(
            source,
            filePath: @"c:\repo\Demo.cs",
            line,
            character);

        Assert.NotNull(result);
        Assert.DoesNotContain("// var ", result.Expression, StringComparison.Ordinal);
        Assert.True(result.Origin.Line >= 0);
        Assert.True(result.Origin.Character >= 0);
    }

    [Fact]
    public void TryExtractLinqExpressionDetailed_HelperCallInSiblingPartialFile_UsesContainingTypeFallback()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "QueryLensTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var callFile = Path.Combine(tempRoot, "Demo.Cn.cs");
            var helperFile = Path.Combine(tempRoot, "Demo.cs");
                        var projectFile = Path.Combine(tempRoot, "Demo.csproj");

                        File.WriteAllText(
                                projectFile,
                                """
                                <Project Sdk="Microsoft.NET.Sdk">
                                    <PropertyGroup>
                                        <TargetFramework>net10.0</TargetFramework>
                                    </PropertyGroup>
                                </Project>
                                """);

            var callSource = """
                using System;
                using System.Linq;
                using System.Linq.Expressions;
                using System.Threading;
                using System.Threading.Tasks;

                public partial class Demo
                {
                    public Task Run(Guid applicationId, CancellationToken ct)
                    {
                        return GetApplicationByIdAsync(
                            applicationId,
                            app => app.ApplicationStatus,
                            ct);
                    }
                }

                public sealed class Application
                {
                    public int ApplicationStatus { get; set; }
                }
                """;

            var helperSource = """
                using System;
                using System.Linq;
                using System.Linq.Expressions;
                using System.Threading;
                using System.Threading.Tasks;

                public partial class Demo
                {
                    public Task<TResult?> GetApplicationByIdAsync<TResult>(
                        Guid applicationId,
                        Expression<Func<Application, TResult>> expression,
                        CancellationToken ct)
                    {
                        return Task.FromResult<TResult?>(dbContext.Applications
                            .Where(w => w.ApplicationId == applicationId)
                            .Select(expression)
                            .SingleOrDefault());
                    }

                    private readonly DemoDbContext dbContext = new();
                }

                public sealed class DemoDbContext
                {
                    public IQueryable<Application> Applications => Array.Empty<Application>().AsQueryable();
                }

                public sealed partial class Application
                {
                    public Guid ApplicationId { get; set; }
                }
                """;

            File.WriteAllText(callFile, callSource);
            File.WriteAllText(helperFile, helperSource);

            var (line, character) = FindPosition(
                callSource,
                """
                            applicationId,
                            app => app.ApplicationStatus,
                """);
            var result = LspSyntaxHelper.TryExtractLinqExpressionDetailed(
                callSource,
                callFile,
                line,
                character);

            Assert.NotNull(result);
            Assert.Equal("dbContext", result.ContextVariableName);
            Assert.Equal("helper-method", result.Origin.Scope);
            Assert.Equal(helperFile, result.Origin.FilePath);
            Assert.Contains("dbContext.Applications", result.Expression, StringComparison.Ordinal);
            Assert.Contains(".Where(w => w.ApplicationId == applicationId)", result.Expression, StringComparison.Ordinal);
            Assert.Contains(".Select(app => app.ApplicationStatus)", result.Expression, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }
}
