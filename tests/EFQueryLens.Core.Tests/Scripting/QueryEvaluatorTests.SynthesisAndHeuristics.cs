using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    [Fact]
    public async Task Evaluate_MissingScalarVariable_InWhere_DoesNotCollapseToWhereFalse()
    {
        var result = await TranslateAsync("db.Users.Where(u => u.Email == companyUen).Select(u => u.Id)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("WHERE FALSE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingBooleanVariable_InLogicalWhere_IsSynthesizedAsBool()
    {
        var result = await TranslateAsync("db.Users.Where(u => isIntranetUser || u.Id > 0).Select(u => u.Id)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Operator '||' cannot be applied", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingGuidAndBoolVariables_InCombinedPredicate_DoNotCrossInfer()
    {
        var result = await TranslateAsync(
            "db.ApplicationChecklists.Where(s => s.ApplicationId == applicationId && (isIntranetUser || s.IsLatest)).Select(s => s.Id)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Operator '==' cannot be applied to operands of type 'Guid' and 'bool'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Operator '||' cannot be applied to operands of type 'object' and 'bool'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingObjectMemberVariable_InWhere_IsSynthesized()
    {
        var result = await TranslateAsync(
            "db.ApplicationChecklists.Where(w => w.ApplicationId == currentUser.ApplicationId).Select(s => new { s.ApplicationId, s.Id })");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not contain a definition", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingObjectWithNowMember_InWhere_UsesDateTimeStub()
    {
        var result = await TranslateAsync(
            "db.Orders.Where(o => o.CreatedAt.Date == dateTime.Now.Date).Select(o => o.Id)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("'string' does not contain a definition for 'Date'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingStringTerm_InContainsStartsWith_IsSynthesizedAsString()
    {
        var result = await TranslateAsync(
            "db.Customers.Where(c => c.Name.ToLower().Contains(term) || c.Email.ToLower().StartsWith(term))");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain(
            "cannot convert from 'object' to 'string'",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingMinOrders_InCountComparison_IsSynthesizedAsNumeric()
    {
        var result = await TranslateAsync(
            "db.Customers.Where(c => c.Orders.Count(o => o.IsNotDeleted) >= minOrders)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain(
            "Operator '>=' cannot be applied to operands of type 'int' and 'object'",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStubDeclaration_GridifyQueryArgument_UsesIGridifyQuery()
    {
        var stub = BuildStubDeclarationForTest(
            missingName: "query",
            expression: "db.Orders.ApplyFilteringAndOrdering(query, gm)");

        Assert.Equal(
            "global::Gridify.IGridifyQuery query = new global::Gridify.GridifyQuery();",
            stub);
    }

    [Fact]
    public void BuildStubDeclaration_GridifyMapperArgument_UsesTypedIGridifyMapper()
    {
        var stub = BuildStubDeclarationForTest(
            missingName: "gm",
            expression: "db.Orders.ApplyFilteringAndOrdering(query, gm)");

        Assert.Equal(
            "global::Gridify.IGridifyMapper<SampleMySqlApp.Domain.Entities.Order>? gm = null;",
            stub);
    }

    [Fact]
    public void BuildStubDeclaration_GridifyQueryWithPagingMembers_PrefersIGridifyQueryOverAnonymousObject()
    {
        var stub = BuildStubDeclarationForTest(
            missingName: "query",
            expression: "db.Orders.ApplyFilteringAndOrdering(query, gm).ApplyPaging(query.Page, query.PageSize)");

        Assert.Equal(
            "global::Gridify.IGridifyQuery query = new global::Gridify.GridifyQuery();",
            stub);
    }

    [Fact]
    public async Task Evaluate_GridifyShape_WithoutGridifyAssembly_UsesFallbackPath()
    {
        var result = await TranslateAsync(
            "db.Orders.ApplyFilteringAndOrdering(query, gm).ApplyPaging(query.Page, query.PageSize)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Orders", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The type or namespace name 'Gridify'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HasMissingGridifyTypeErrors_NonGridifyMissingType_ReturnsFalse()
    {
        var errors = CreateCompilationErrors(
            "public sealed class C { private MissingType _x = default!; }");

        Assert.Contains(errors, d => d.Id == "CS0246");
        Assert.False(InvokeHasMissingGridifyTypeErrors(errors));
    }

    [Fact]
    public void HasMissingGridifyTypeErrors_GridifyMissingType_ReturnsTrue()
    {
        var errors = CreateCompilationErrors(
            "public sealed class C { private Gridify.GridifyQuery _x = default!; }");

        Assert.Contains(errors, d => d.Id == "CS0246" || d.Id == "CS0234");
        Assert.True(InvokeHasMissingGridifyTypeErrors(errors));
    }

    [Fact]
    public void BuildStubDeclaration_IsPatternEnumMember_UsesEnumTypeFromPattern()
    {
        var stub = BuildStubDeclarationForTest(
            missingName: "pastPlanningPlusCase",
            expression: "db.Orders.Where(o => o.UserId == ((pastPlanningPlusCase.CaseType is System.DayOfWeek.Monday or System.DayOfWeek.Tuesday) ? 1 : 2)).Select(o => o.Id)");

        Assert.Equal(
            "var pastPlanningPlusCase = new { CaseType = (System.DayOfWeek)1 };",
            stub);
    }

    [Fact]
    public void BuildStubDeclaration_StringMethodArgument_UsesStringStub()
    {
        var stub = BuildStubDeclarationForTest(
            missingName: "term",
            expression: "db.Customers.Where(c => c.Name.ToLower().Contains(term) || c.Email.ToLower().StartsWith(term))");

        Assert.Equal("string term = \"qlstub0\";", stub);
    }

    [Fact]
    public void BuildStubDeclaration_CountComparisonVariable_UsesNumericStub()
    {
        var stub = BuildStubDeclarationForTest(
            missingName: "minOrders",
            expression: "db.Customers.Where(c => c.Orders.Count(o => o.IsNotDeleted) >= minOrders)");

        Assert.Equal("int minOrders = 1;", stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalVariableStaticType_FallsThroughToHeuristics()
    {
        // When the LSP hint resolves to a static type ("Math"), the stub for Math itself
        // (not page/pageSize) should fall through to heuristics and end up as object rather
        // than causing an empty/hard-block. The key invariant is that page/pageSize are
        // synthesized correctly via Skip/Take heuristics - tested by the Evaluate_ tests.
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "Math",
            expression: "db.Orders.Skip(Math.Max(page, 1)).Take(pageSize)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Math"] = "System.Math"
            });

        Assert.NotNull(stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalVariableAliasToStaticType_FallsThroughToHeuristics()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "mathHelper",
            expression: "db.Orders.Skip(pageSize)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mathHelper"] = "MathAlias"
            },
            usingAliases: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MathAlias"] = "System.Math"
            });

        Assert.NotNull(stub);
    }

    [Fact]
    public void BuildStubDeclaration_UnresolvedTypeMarkerQuestionMark_FallsThroughToHeuristics()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "unknownType",
            expression: "db.Orders.Skip(pageSize)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["unknownType"] = "?"
            });

        Assert.NotNull(stub);
    }

    [Fact]
    public async Task Evaluate_PagingWithMathCall_DoesNotSurfaceStaticTypeCompilationErrors()
    {
        var result = await TranslateAsync(
            "db.Orders.OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id).Skip(Math.Max(pageSize * pageIndex, 0)).Take(pageSize).Select(expression)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Cannot declare a variable of static type 'Math'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cannot convert to static type 'Math'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_PagingWithMathCall_WithStaticLspHint_FallsThroughToNumericHeuristic()
    {
        var result = await _evaluator.EvaluateAsync(_alcCtx, new TranslationRequest
        {
            AssemblyPath = _alcCtx.AssemblyPath,
            Expression = "db.Orders.OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(expression)",
            LocalVariableTypes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["page"] = "Math",
                ["pageSize"] = "Math",
            },
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Unknown variable 'page'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unknown variable 'pageSize'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingPagingVariables_InSkipTakeArithmetic_AreSynthesizedAsNumeric()
    {
        var result = await TranslateAsync(
            "db.Orders.OrderBy(o => o.Id).Skip(pageSize * pageIndex).Take(pageSize).Select(o => o.Id)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain(
            "Operator '*' cannot be applied to operands of type 'object' and 'object'",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_PatternTernaryComparisonWithoutParentheses_IsNormalizedForIntendedComparison()
    {
        var result = await TranslateAsync(
            "db.Orders.Where(o => o.UserId == selector.Value is 1 or 2 ? 1 : 2).Select(o => o.Id)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain(
            "Operator '==' cannot be applied to operands of type 'int' and 'bool'",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_PatternTernaryComparisonInsideLogicalAnd_IsNormalizedForIntendedComparison()
    {
        var result = await TranslateAsync(
            "db.Orders.Where(o => o.Id > 0 && o.UserId == selector.Value is 1 or 2 ? 1 : 2).Select(o => o.Id)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain(
            "Operator '==' cannot be applied to operands of type 'int' and 'bool'",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WideProjection_DoesNotFailWithIndexOutOfRange()
    {
        var projectionMembers = string.Join(", ", Enumerable.Range(1, 64).Select(i => $"C{i} = u.Id"));
        var expression = $"db.Users.Select(u => new {{ {projectionMembers} }})";

        var result = await TranslateAsync(expression);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Index was outside the bounds of the array", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingCollectionVariable_InContains_DoesNotFallBackToObject()
    {
        var result = await TranslateAsync("db.Orders.Where(o => userIds.Contains(o.UserId))");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("'object' does not contain a definition for 'Contains'",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingCollectionVariable_InContains_DoesNotCollapseToWhereFalse()
    {
        var result = await TranslateAsync("db.Orders.Where(o => userIds.Contains(o.UserId))");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("WHERE FALSE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingGuidCollectionVariable_InContains_UsesAtLeastTwoPlaceholderValues()
    {
        var result = await TranslateAsync(
            "db.ApplicationChecklists.Where(c => listingIds.Contains(c.ApplicationId))");

        Assert.True(result.Success, result.ErrorMessage);

        var secondGuid = "00000000-0000-0000-0000-000000000001";
        var hasSecondGuidInSql = (result.Sql ?? string.Empty)
            .Contains(secondGuid, StringComparison.OrdinalIgnoreCase);
        var hasSecondGuidInParameters = result.Parameters.Any(p =>
            (p.InferredValue ?? string.Empty)
                .Contains(secondGuid, StringComparison.OrdinalIgnoreCase));

        Assert.True(
            hasSecondGuidInSql || hasSecondGuidInParameters,
            "Expected synthesized Contains placeholders to include at least two GUID values.");
    }

    [Fact]
    public async Task Evaluate_MissingSelectorVariable_InSelect_IsSynthesized()
    {
        var result = await TranslateAsync("db.Orders.Select(selector)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
    }

    [Fact]
    public async Task Evaluate_MissingWhereAndSelectExpressionVariables_AreSynthesized()
    {
        var result = await TranslateAsync("db.Orders.Where(filter).Select(expression)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingCancellationToken_InAsyncTerminal_IsSynthesized()
    {
        var result = await TranslateAsync("db.Orders.SingleOrDefaultAsync(ct)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
