using System.Reflection;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Scripting;

namespace EFQueryLens.Core.Tests.Scripting;

/// <summary>
/// Integration-style unit tests for <see cref="QueryEvaluator"/>.
///
/// Sample fixtures are copied into isolated subfolders under the test output dir
/// so transitive package DLLs do not overwrite each other.
/// </summary>
[Collection("AssemblyLoadContextIsolation")]
public class QueryEvaluatorTests : IDisposable
{
    private readonly ProjectAssemblyContext _alcCtx;
    private readonly QueryEvaluator _evaluator;

    public QueryEvaluatorTests()
    {
        _alcCtx    = new ProjectAssemblyContext(GetSampleAppDll());
        _evaluator = new QueryEvaluator();
    }

    public void Dispose() => _alcCtx.Dispose();

    // ─── Helper ───────────────────────────────────────────────────────────────

    private static string GetSampleAppDll()
    {
        var dir = Path.GetDirectoryName(typeof(QueryEvaluatorTests).Assembly.Location)!;
        var dll = ResolveSampleDll(dir, "SampleApp", "SampleApp.dll");

        if (!File.Exists(dll))
            throw new FileNotFoundException(
                $"SampleApp.dll not found in test output dir. Expected: {dll}");

        return dll;
    }

    private static string GetSampleSqlServerAppDll()
    {
        var dir = Path.GetDirectoryName(typeof(QueryEvaluatorTests).Assembly.Location)!;
        var dll = ResolveSampleDll(dir, "SampleSqlServerApp", "SampleSqlServerApp.dll");

        if (!File.Exists(dll))
            throw new FileNotFoundException(
                $"SampleSqlServerApp.dll not found in test output dir. Expected: {dll}");

        return dll;
    }

    private static string ResolveSampleDll(string testOutputDir, string sampleFolder, string dllName)
    {
        var isolated = Path.Combine(testOutputDir, sampleFolder, dllName);
        if (File.Exists(isolated))
            return isolated;

        // Backward compatibility for older builds that copied files into root.
        return Path.Combine(testOutputDir, dllName);
    }

    private Task<QueryTranslationResult> TranslateAsync(
        string expression,
        string? dbContextTypeName = null,
        IReadOnlyList<string>? additionalImports = null,
        IReadOnlyDictionary<string, string>? usingAliases = null,
        IReadOnlyList<string>? usingStaticTypes = null,
        CancellationToken ct = default) =>
        _evaluator.EvaluateAsync(_alcCtx,
            new TranslationRequest
            {
                AssemblyPath      = _alcCtx.AssemblyPath,
                Expression        = expression,
                DbContextTypeName = dbContextTypeName,
                AdditionalImports = additionalImports ?? [],
                UsingAliases = usingAliases
                    ?? new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = usingStaticTypes ?? [],
            }, ct);

    // ─── Basic translation ────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_SimpleDbSet_ReturnsSql()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Orders", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_SqlServerSample_SimpleDbSet_ReturnsSql()
    {
        using var sqlAlcCtx = new ProjectAssemblyContext(GetSampleSqlServerAppDll());
        var evaluator = new QueryEvaluator();

        var result = await evaluator.EvaluateAsync(
            sqlAlcCtx,
            new TranslationRequest
            {
                AssemblyPath = sqlAlcCtx.AssemblyPath,
                Expression = "db.Customers",
                DbContextTypeName = "SampleSqlServerApp.SqlServerAppDbContext",
            });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Customers", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", result.Metadata.ProviderName);
    }

    [Fact]
    public async Task Evaluate_SimpleDbSet_PopulatesCommandsList()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Commands);
        Assert.Contains("Orders", result.Commands[0].Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WhereClause_ContainsWhere()
    {
        var result = await TranslateAsync("db.Orders.Where(o => o.UserId == 5)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("WHERE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_ExplicitDbContextName_Resolves()
    {
        var result = await TranslateAsync("db.Users", dbContextTypeName: "AppDbContext");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Users", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_FullyQualifiedDbContextName_Resolves()
    {
        var result = await TranslateAsync("db.Users", dbContextTypeName: "SampleApp.AppDbContext");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
    }

    [Fact]
    public async Task Evaluate_MultipleEntities_EachReturnsSql()
    {
        string[] expressions = ["db.Orders", "db.Users", "db.Products", "db.Categories"];

        foreach (var expr in expressions)
        {
            var result = await TranslateAsync(expr);
            Assert.True(result.Success, $"Failed for '{expr}': {result.ErrorMessage}");
            Assert.NotNull(result.Sql);
        }
    }

    // ─── Result shape ─────────────────────────────────────────────────────────

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
        Assert.Equal("SampleApp.AppDbContext", result.Metadata.DbContextType);
    }

    [Fact]
    public async Task Evaluate_Metadata_EfCoreVersionIsKnown()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEqual("unknown", result.Metadata.EfCoreVersion);
    }

    [Fact]
    public async Task Evaluate_WhereWithParam_ParsesParameters()
    {
        var result = await TranslateAsync("db.Orders.Where(o => o.UserId == 5)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);

        // EF Core 9 + Pomelo may inline the constant 5 directly into the SQL
        // (e.g. "WHERE `UserId` = 5") rather than emitting a @p0 parameter.
        // Either way the value must appear in the SQL and the result must succeed.
        if (result.Parameters.Count > 0)
        {
            // Older behaviour: parameterised constant
            var p = result.Parameters[0];
            Assert.StartsWith("@", p.Name);
            Assert.Equal("5", p.InferredValue);
        }
        else
        {
            // EF Core 9 behaviour: inlined literal
            Assert.Contains("5", result.Sql, StringComparison.Ordinal);
        }
    }

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
    public async Task Evaluate_RootWrapperContextHop_IsNormalizedFromCompilerDiagnostics()
    {
        var result = await TranslateAsync("services.Context.Orders.Select(o => o.Id)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain(
            "does not contain a definition for 'Context'",
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
            "global::Gridify.IGridifyMapper<SampleApp.Entities.Order>? gm = null;",
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
    public async Task Evaluate_ChecklistSelectManyVariant_ReturnsSql()
    {
        var result = await TranslateAsync(
            "db.ApplicationChecklists.AsNoTracking()" +
            ".Where(w => !w.IsDeleted && w.IsLatest)" +
            ".Where(w => w.ApplicationId == applicationId)" +
            ".SelectMany(x => x.ChecklistChangeTypes)" +
            ".Where(w => !w.IsDeleted)" +
            ".Select(s => s.ChangeType)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("ApplicationChecklists", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ApplicationChecklistChangeTypes", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_ChecklistSelectManyVariant_DoesNotSurfaceBufferedReaderFieldCountFailure()
    {
        var result = await TranslateAsync(
            "db.ApplicationChecklists.AsNoTracking()" +
            ".Where(w => !w.IsDeleted && w.IsLatest)" +
            ".Where(w => w.ApplicationId == applicationId)" +
            ".SelectMany(x => x.ChecklistChangeTypes)" +
            ".Where(w => !w.IsDeleted)" +
            ".Select(s => s.ChangeType)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain(
            "underlying reader doesn't have as many fields",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_ExpressionSelectorNestedToList_EmitsPartialRiskWarning()
    {
        var result = await TranslateAsync(
            "db.ApplicationChecklists.AsNoTracking()" +
            ".Where(w => !w.IsDeleted && w.IsLatest)" +
            ".Where(w => w.ApplicationId == applicationId)" +
            ".Select(app => new {" +
            "    ChangeTypes = app.ChecklistChangeTypes" +
            "        .Where(t => !t.IsDeleted)" +
            "        .Select(t => t.ChangeType)" +
            "        .ToList()" +
            "})");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains(
            result.Warnings,
            w => string.Equals(w.Code, "QL_EXPRESSION_PARTIAL_RISK", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_MissingCancellationToken_InAsyncTerminal_IsSynthesized()
    {
        var result = await TranslateAsync("db.Orders.SingleOrDefaultAsync(ct)");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("IQueryable", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WithAliasUsingContext_CanResolveAliasedTypeMember()
    {
        var result = await TranslateAsync(
            "db.Orders.Where(o => o.UserId < IntAlias.MaxValue)",
            usingAliases: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["IntAlias"] = "System.Int32"
            });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WithStaticUsingContext_CanResolveStaticMethodCall()
    {
        var result = await TranslateAsync(
            "db.Orders.Where(o => o.UserId < Abs(-5))",
            usingStaticTypes: ["System.Math"]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WithUnresolvableAdditionalImport_DoesNotFailCompilation()
    {
        var result = await TranslateAsync(
            "db.Orders.Where(o => o.UserId > 0)",
            additionalImports: ["Microsoft.AspNetCore.Http"]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingAliasIdentifier_IsNotSynthesizedAsObjectVariable()
    {
        var result = await TranslateAsync(
            "db.Orders.Where(o => o.UserId == Enums.Approved)",
            usingAliases: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Enums"] = "SampleApp.Does.Not.Exist"
            });

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.DoesNotContain("'object' does not contain a definition", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Error handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_InvalidExpression_ReturnsFailure()
    {
        var result = await TranslateAsync("this is not valid C#");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.True(
            result.ErrorMessage.Contains("error", StringComparison.OrdinalIgnoreCase)
            || result.ErrorMessage.Contains("No DbContext subclass found", StringComparison.OrdinalIgnoreCase),
            $"Unexpected error message: {result.ErrorMessage}");
    }

    [Fact]
    public async Task Evaluate_NonQueryableExpression_ReturnsFailure()
    {
        var result = await TranslateAsync("42");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("IQueryable", result.ErrorMessage);
    }

    [Fact]
    public async Task Evaluate_UnknownDbContextName_ReturnsFailure()
    {
        var result = await TranslateAsync("db.Orders", dbContextTypeName: "NoSuchContext");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("NoSuchContext", result.ErrorMessage);
    }

    [Fact]
    public async Task Evaluate_TopLevelServiceMethodInvocation_ReturnsClearUnsupportedMessage()
    {
        var result = await TranslateAsync("service.GetWorkflowByTypeAsync(workflowType, expression, ct)");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Top-level method invocations", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ─── ScriptState cache ────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_SecondCall_IsNotSlowerByOrderOfMagnitude()
    {
        // Cold call — compiles the script
        var r1 = await TranslateAsync("db.Orders");
        Assert.True(r1.Success, r1.ErrorMessage);

        // Warm call — should hit cached ScriptState
        var r2 = await TranslateAsync("db.Users");
        Assert.True(r2.Success, r2.ErrorMessage);

        // The warm call should complete in reasonable time.
        // We don't assert it's *faster* (CI jitter), just that it succeeded
        // and took less than 10s (the cold call could be 1-2s on first Roslyn compile).
        Assert.True(r2.Metadata.TranslationTime < TimeSpan.FromSeconds(10));
    }

    // ─── IQueryLensDbContextFactory + IDesignTimeDbContextFactory discovery ─────

    [Fact]
    public async Task Evaluate_WhenQueryLensFactoryExists_StrategyIsQueryLensFactory()
    {
        // SampleApp ships AppQueryLensFactory : IQueryLensDbContextFactory<AppDbContext>.
        // QueryEvaluator must report "querylens-factory" as the creation strategy.
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("querylens-factory", result.Metadata.CreationStrategy);
    }

    [Fact]
    public async Task Evaluate_QueryLensFactory_AttemptsExecutionCapture()
    {
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Commands);
        Assert.DoesNotContain(result.Warnings, w => w.Code == "QL_CAPTURE_SKIPPED");
    }

    [Fact]
    public async Task Evaluate_QueryLensFactory_TakesPriorityOverDesignTimeFactory()
    {
        // SampleApp has BOTH AppQueryLensFactory (IQueryLensDbContextFactory<T>) AND
        // AppDbContextFactory (IDesignTimeDbContextFactory<T>).
        // Priority 0 (QueryLens factory) must win over Priority 1 (design-time factory).
        var result = await TranslateAsync("db.Users");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("querylens-factory", result.Metadata.CreationStrategy);
        Assert.NotEqual("design-time-factory", result.Metadata.CreationStrategy);
        Assert.NotEqual("bootstrap", result.Metadata.CreationStrategy);
    }

    [Fact]
    public async Task Evaluate_WhenDesignTimeFactoryExists_UsesFactoryOverBootstrap()
    {
        // SampleApp now has a QueryLens factory (Priority 0) that wins over the
        // design-time factory (Priority 1). Strategy must NOT be "bootstrap".
        var result = await TranslateAsync("db.Orders");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Orders", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("querylens-factory", result.Metadata.CreationStrategy);
    }

    [Fact]
    public async Task Evaluate_WhenDesignTimeFactoryExists_SqlIsStillGenerated()
    {
        // Verify that using the factory does not break SQL generation for
        // WHERE clauses or any other operator.
        var result = await TranslateAsync("db.Orders.Where(o => o.UserId == 5)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("WHERE", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("querylens-factory", result.Metadata.CreationStrategy);
    }

    [Fact]
    public async Task Evaluate_ExistingTests_StillPassWithFactoryPath()
    {
        // All four entity sets must translate correctly when any factory is active.
        string[] expressions = ["db.Orders", "db.Users", "db.Products", "db.Categories"];

        foreach (var expr in expressions)
        {
            var result = await TranslateAsync(expr);
            Assert.True(result.Success, $"Failed for '{expr}': {result.ErrorMessage}");
            Assert.NotNull(result.Sql);
            Assert.Equal("querylens-factory", result.Metadata.CreationStrategy);
        }
    }

    private string BuildStubDeclarationForTest(string missingName, string expression)
    {
        var dbContextType = _alcCtx.FindDbContextType(null, expression);
        var request = new TranslationRequest
        {
            AssemblyPath = _alcCtx.AssemblyPath,
            Expression = expression,
        };

        var method = typeof(QueryEvaluator).GetMethod(
            "BuildStubDeclaration",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var stub = method!.Invoke(
            null,
            [missingName, "db", request, dbContextType]) as string;

        Assert.False(string.IsNullOrWhiteSpace(stub));
        return stub!;
    }
}
