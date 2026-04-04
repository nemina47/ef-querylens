using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public partial class LspSyntaxHelperTests
{
    [Fact]
    public void TryBuildV2ExtractionPlan_DirectTerminalChain_ReturnsMaterializedBoundary()
    {
        var source = """
            _ = dbContext.Users
                .Where(u => u.IsActive)
                .ToListAsync(ct);
            """;

        var (line, character) = FindPosition(source, "ToListAsync");

        var success = LspSyntaxHelper.TryBuildV2ExtractionPlan(
            source,
            @"c:\repo\Demo.cs",
            line,
            character,
            out var plan,
            out var diagnostics);

        Assert.True(success);
        Assert.NotNull(plan);
        Assert.Empty(diagnostics);
        Assert.Equal(V2QueryBoundaryKind.Materialized, plan.BoundaryKind);
        Assert.False(plan.NeedsMaterialization);
        Assert.Equal("dbContext", plan.RootContextVariableName);
        Assert.Equal("Users", plan.RootMemberName);
    }

    [Fact]
    public void TryBuildV2ExtractionPlan_QueryExpressionWithoutTerminal_ReturnsQueryableBoundary()
    {
        var source = """
            var q = from u in dbContext.Users
                    where u.IsActive
                    select u;
            """;

        var (line, character) = FindPosition(source, "where u.IsActive");

        var success = LspSyntaxHelper.TryBuildV2ExtractionPlan(
            source,
            @"c:\repo\Demo.cs",
            line,
            character,
            out var plan,
            out var diagnostics);

        Assert.True(success);
        Assert.NotNull(plan);
        Assert.Empty(diagnostics);
        Assert.Equal(V2QueryBoundaryKind.Queryable, plan.BoundaryKind);
        Assert.True(plan.NeedsMaterialization);
    }

    [Fact]
    public void TryBuildV2ExtractionPlan_DirectQueryableHelperInlineShape_ReturnsSuccess()
    {
        var source = """
            class Demo
            {
                IQueryable<User> GetActiveUsers()
                {
                    return dbContext.Users.Where(u => u.IsActive);
                }

                void Run(CancellationToken ct)
                {
                    _ = GetActiveUsers().Where(u => u.Id > 0).ToListAsync(ct);
                }
            }
            """;

        var (line, character) = FindPosition(source, "GetActiveUsers().Where");

        var success = LspSyntaxHelper.TryBuildV2ExtractionPlan(
            source,
            @"c:\repo\Demo.cs",
            line,
            character,
            out var plan,
            out var diagnostics);

        Assert.True(success);
        Assert.NotNull(plan);
        Assert.Empty(diagnostics);
        Assert.Contains("GetActiveUsers", plan.AppliedHelperMethods, StringComparer.Ordinal);
    }

    [Fact]
    public void TryBuildV2ExtractionPlan_MultiExpressionHelperInlineShape_ReturnsSuccess()
    {
        var source = """
            class Demo
            {
                IQueryable<TResult> GetSomeQueryById<TResult>(
                    Guid id,
                    Expression<Func<User, bool>> whereExpression,
                    Expression<Func<User, TResult>> selectExpression)
                {
                    return dbContext.Users
                        .Where(u => u.Id == id)
                        .Where(whereExpression)
                        .Select(selectExpression);
                }

                void Run(Guid id, CancellationToken ct)
                {
                    _ = GetSomeQueryById(
                            id,
                            u => u.IsActive,
                            u => new { u.Id, u.Name })
                        .ToListAsync(ct);
                }
            }
            """;

        var (line, character) = FindPosition(source, "GetSomeQueryById(");

        var success = LspSyntaxHelper.TryBuildV2ExtractionPlan(
            source,
            @"c:\repo\Demo.cs",
            line,
            character,
            out var plan,
            out var diagnostics);

        Assert.True(success);
        Assert.NotNull(plan);
        Assert.Empty(diagnostics);
        Assert.Contains("GetSomeQueryById", plan.AppliedHelperMethods, StringComparer.Ordinal);
    }

    [Fact]
    public void TryBuildV2ExtractionPlan_UnsupportedControlFlowHelper_ReturnsDiagnostic()
    {
        var source = """
            class Demo
            {
                IQueryable<User> GetUsers(bool includeInactive)
                {
                    if (includeInactive)
                    {
                        return dbContext.Users;
                    }

                    return dbContext.Users.Where(u => u.IsActive);
                }

                void Run(CancellationToken ct)
                {
                    _ = GetUsers(false).ToListAsync(ct);
                }
            }
            """;

        var (line, character) = FindPosition(source, "GetUsers(false)");

        var success = LspSyntaxHelper.TryBuildV2ExtractionPlan(
            source,
            @"c:\repo\Demo.cs",
            line,
            character,
            out var plan,
            out var diagnostics);

        Assert.False(success);
        Assert.Null(plan);
        Assert.Contains(diagnostics, d => d.Code == "QLV2_UNSUPPORTED_HELPER_CONTROL_FLOW");
    }
}