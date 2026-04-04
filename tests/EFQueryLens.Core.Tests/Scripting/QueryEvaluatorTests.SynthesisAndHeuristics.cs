using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    [Fact]
    public async Task Evaluate_MissingScalarVariable_InWhere_DoesNotCollapseToWhereFalse()
    {
        var result = await TranslateStrictAsync(
            "db.Users.Where(u => u.Email == companyUen).Select(u => u.Id)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["companyUen"] = "string",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("WHERE FALSE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingBooleanVariable_InLogicalWhere_IsSynthesizedAsBool()
    {
        var result = await TranslateStrictAsync(
            "db.Users.Where(u => isIntranetUser || u.Id > 0).Select(u => u.Id)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["isIntranetUser"] = "bool",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Operator '||' cannot be applied", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingGuidAndBoolVariables_InCombinedPredicate_DoNotCrossInfer()
    {
        var result = await TranslateStrictAsync(
            "db.ApplicationChecklists.Where(s => s.ApplicationId == applicationId && (isIntranetUser || s.IsLatest)).Select(s => s.Id)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["applicationId"] = "System.Guid",
                ["isIntranetUser"] = "bool",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Operator '==' cannot be applied to operands of type 'Guid' and 'bool'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Operator '||' cannot be applied to operands of type 'object' and 'bool'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingObjectMemberVariable_InWhere_IsSynthesized()
    {
        var result = await TranslateStrictAsync(
            "db.ApplicationChecklists.Where(w => w.ApplicationId == currentUser.ApplicationId).Select(s => new { s.ApplicationId, s.Id })",
            memberTypeHints:
            [
                new MemberTypeHint { ReceiverName = "currentUser", MemberName = "ApplicationId", TypeName = "System.Guid" },
            ],
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("CS0103", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("currentUser", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingObjectWithNowMember_InWhere_UsesDateTimeStub()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.Where(o => o.CreatedAt.Date == dateTime.Now.Date).Select(o => o.Id)",
            memberTypeHints:
            [
                new MemberTypeHint { ReceiverName = "dateTime", MemberName = "Now", TypeName = "System.DateTime" },
            ],
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("CS0103", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dateTime", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingStringTerm_InContainsStartsWith_IsSynthesizedAsString()
    {
        var result = await TranslateStrictAsync(
            "db.Customers.Where(c => c.Name.ToLower().Contains(term) || c.Email.ToLower().StartsWith(term))",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["term"] = "string",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain(
            "cannot convert from 'object' to 'string'",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStubDeclaration_NullableLikeMembers_UsesBoolHasValueAndTypedValue()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "minTotal",
            expression: "db.Orders.Where(o => (!minTotal.HasValue || o.Total >= minTotal.Value))",
            memberTypeHints:
            [
                new MemberTypeHint { ReceiverName = "minTotal", MemberName = "HasValue", TypeName = "bool" },
                new MemberTypeHint { ReceiverName = "minTotal", MemberName = "Value", TypeName = "decimal" },
            ]);

        Assert.Equal(string.Empty, stub);
    }

    [Fact]
    public async Task Evaluate_MissingNullableLikeObjects_InPredicate_DoesNotUseStringHasValue()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.Where(o => (!minTotal.HasValue || o.Total >= minTotal.Value) && (!status.HasValue || o.Status == status.Value)).Select(o => o.Id)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["minTotal"] = "decimal?",
                ["status"] = "SampleMySqlApp.Domain.Enums.OrderStatus?",
            },
            memberTypeHints:
            [
                new MemberTypeHint { ReceiverName = "minTotal", MemberName = "HasValue", TypeName = "bool" },
                new MemberTypeHint { ReceiverName = "minTotal", MemberName = "Value", TypeName = "decimal" },
                new MemberTypeHint { ReceiverName = "status", MemberName = "HasValue", TypeName = "bool" },
                new MemberTypeHint { ReceiverName = "status", MemberName = "Value", TypeName = "SampleMySqlApp.Domain.Enums.OrderStatus" },
            ],
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain(
            "Operator '!' cannot be applied to operand of type 'string'",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStubDeclaration_MemberTypeHints_ArePreferredOverNameHeuristics()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "range",
            expression: "db.Orders.Where(o => !range.HasValue || o.Total >= range.Value)",
            localSymbolHints:
            [
                new LocalSymbolHint { Name = "range", TypeName = "decimal?", Kind = "parameter" },
            ],
            memberTypeHints:
            [
                new MemberTypeHint { ReceiverName = "range", MemberName = "HasValue", TypeName = "bool" },
                new MemberTypeHint { ReceiverName = "range", MemberName = "Value", TypeName = "decimal" },
            ]);

        Assert.Equal("decimal? range = 1m;", stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalSymbolHintNullableDecimal_UsesNullableDecimalStub()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "minTotal",
            expression: "db.Orders.Where(o => !minTotal.HasValue || o.Total >= minTotal.Value)",
            localSymbolHints:
            [
                new LocalSymbolHint { Name = "minTotal", TypeName = "decimal?", Kind = "lambda-parameter" },
            ]);

        Assert.Equal("decimal? minTotal = 1m;", stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalSymbolHintWithInitializer_UsesInitializerExpression()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "pageSize",
            expression: "db.Orders.Skip((page - 1) * pageSize).Take(pageSize)",
            localSymbolHints:
            [
                new LocalSymbolHint
                {
                    Name = "pageSize",
                    TypeName = "int",
                    Kind = "local",
                    InitializerExpression = "Math.Max(request.PageSize, 1)",
                },
            ]);

        Assert.Equal("var pageSize = Math.Max(request.PageSize, 1);", stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalSymbolHintWithNullableValueInitializer_FallsBackToTypedPlaceholder()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "fromUtc",
            expression: "db.Orders.Where(o => o.CreatedUtc >= fromUtc)",
            localSymbolHints:
            [
                new LocalSymbolHint
                {
                    Name = "fromUtc",
                    TypeName = "DateTime",
                    Kind = "local",
                    InitializerExpression = "request.CreatedAfterUtc.Value",
                },
            ]);

        Assert.Equal("System.DateTime fromUtc = System.DateTime.UtcNow;", stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalSymbolHintWithInstanceCallInitializer_FallsBackToTypedPlaceholder()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "term",
            expression: "db.Orders.Where(o => o.Notes != null && o.Notes.ToLower().Contains(term))",
            localSymbolHints:
            [
                new LocalSymbolHint
                {
                    Name = "term",
                    TypeName = "string",
                    Kind = "local",
                    InitializerExpression = "request.NotesSearch.Trim().ToLowerInvariant()",
                },
            ]);

        Assert.Equal("string term = \"\";", stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalCollectionInitializer_QualifiesTypeChainsFromHint()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "categoryList",
            expression: "db.Orders.Where(o => categoryList.Contains(o.Status))",
            localSymbolHints:
            [
                new LocalSymbolHint
                {
                    Name = "categoryList",
                    TypeName = "global::System.Collections.Generic.List<global::Sla.Plus.Domain.Enums.MlrRequestCategory>",
                    Kind = "local",
                    InitializerExpression = "new List<Domain.Enums.MlrRequestCategory> { Domain.Enums.MlrRequestCategory.Api }",
                },
            ]);

        Assert.Equal(
            "var categoryList = new List<Sla.Plus.Domain.Enums.MlrRequestCategory> { Sla.Plus.Domain.Enums.MlrRequestCategory.Api };",
            stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalNullableEnumCollectionInitializer_DoesNotProduceInvalidNullableTypeAccess()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "checkTransactionDateStatus",
            expression: "db.Orders.Where(o => checkTransactionDateStatus.Contains(o.Status))",
            localSymbolHints:
            [
                new LocalSymbolHint
                {
                    Name = "checkTransactionDateStatus",
                    TypeName = "global::System.Collections.Generic.List<global::Sla.Plus.Domain.Enums.GssDemolitionSubmissionStatus?>",
                    Kind = "local",
                    InitializerExpression = "new List<Enums.GssDemolitionSubmissionStatus?> { Enums.GssDemolitionSubmissionStatus.Defunct }",
                },
            ]);

        Assert.DoesNotContain("??>", stub, StringComparison.Ordinal);
        Assert.DoesNotContain("?.Defunct", stub, StringComparison.Ordinal);
        Assert.Contains("new List<Sla.Plus.Domain.Enums.GssDemolitionSubmissionStatus?>", stub, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStubDeclaration_LocalCollectionExpressionInitializer_UsesTypedAssignment()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "validAppStatus",
            expression: "db.Orders.Where(o => validAppStatus.Contains(o.Status)).Select(o => o.Id)",
            localSymbolHints:
            [
                new LocalSymbolHint
                {
                    Name = "validAppStatus",
                    TypeName = "System.Collections.Generic.List<SampleMySqlApp.Domain.Enums.OrderStatus>",
                    Kind = "local",
                    InitializerExpression = "[SampleMySqlApp.Domain.Enums.OrderStatus.Pending]",
                },
            ]);

        Assert.StartsWith(
            "System.Collections.Generic.List<SampleMySqlApp.Domain.Enums.OrderStatus> validAppStatus = [",
            stub,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStubDeclaration_InterfaceType_UsesInterfaceProxyStub()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "clock",
            expression: "db.Orders.Where(o => o.CreatedAt <= clock.Now)",
            localSymbolHints:
            [
                new LocalSymbolHint
                {
                    Name = "clock",
                    TypeName = "System.IFormatProvider",
                    Kind = "parameter",
                },
            ]);

        Assert.Equal("var clock = __CreateInterfaceProxy__<System.IFormatProvider>();", stub);
    }

    [Fact]
    public async Task Evaluate_MissingMinOrders_InCountComparison_IsSynthesizedAsNumeric()
    {
        var result = await TranslateStrictAsync(
            "db.Customers.Where(c => c.Orders.Count(o => o.IsNotDeleted) >= minOrders)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["minOrders"] = "int",
            },
            ct: TestContext.Current.CancellationToken);

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
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "query",
            expression: "db.Orders.ApplyFilteringAndOrdering(query, gm)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = "global::Gridify.IGridifyQuery",
            });

        Assert.Equal(
            "global::Gridify.IGridifyQuery query = new global::Gridify.GridifyQuery();",
            stub);
    }

    [Fact]
    public void BuildStubDeclaration_GridifyMapperArgument_UsesTypedIGridifyMapper()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "gm",
            expression: "db.Orders.ApplyFilteringAndOrdering(query, gm)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["gm"] = "global::Gridify.IGridifyMapper<SampleMySqlApp.Domain.Entities.Order>",
            });

        Assert.Equal(
            "global::Gridify.IGridifyMapper<SampleMySqlApp.Domain.Entities.Order> gm = null!;",
            stub);
    }

    [Fact]
    public void BuildStubDeclaration_GridifyQueryWithPagingMembers_PrefersIGridifyQueryOverAnonymousObject()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "query",
            expression: "db.Orders.ApplyFilteringAndOrdering(query, gm).ApplyPaging(query.Page, query.PageSize)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = "global::Gridify.IGridifyQuery",
            });

        Assert.Equal(
            "global::Gridify.IGridifyQuery query = new global::Gridify.GridifyQuery();",
            stub);
    }

    [Fact]
    public async Task Evaluate_GridifyShape_WithoutHints_FailsInStrictMode()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.ApplyFilteringAndOrdering(query, gm).ApplyPaging(query.Page, query.PageSize)",
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("CS0103", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStubDeclaration_IsPatternEnumMember_UsesEnumTypeFromPattern()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "pastPlanningPlusCase",
            expression: "db.Orders.Where(o => o.UserId == ((pastPlanningPlusCase.CaseType is System.DayOfWeek.Monday or System.DayOfWeek.Tuesday) ? 1 : 2)).Select(o => o.Id)",
            memberTypeHints:
            [
                new MemberTypeHint { ReceiverName = "pastPlanningPlusCase", MemberName = "CaseType", TypeName = "System.DayOfWeek" },
            ]);

        Assert.Equal(string.Empty, stub);
    }

    [Fact]
    public void BuildStubDeclaration_StringMethodArgument_UsesStringStub()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "term",
            expression: "db.Customers.Where(c => c.Name.ToLower().Contains(term) || c.Email.ToLower().StartsWith(term))",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["term"] = "string",
            });

        Assert.Equal("string term = \"\";", stub);
    }

    [Fact]
    public void BuildStubDeclaration_CountComparisonVariable_UsesNumericStub()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "minOrders",
            expression: "db.Customers.Where(c => c.Orders.Count(o => o.IsNotDeleted) >= minOrders)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["minOrders"] = "int",
            });

        Assert.Equal("int minOrders = 0;", stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalVariableStaticType_StrictModeReturnsEmptyStub()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "Math",
            expression: "db.Orders.Skip(Math.Max(page, 1)).Take(pageSize)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Math"] = "System.Math"
            });

        Assert.Equal(string.Empty, stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalVariableAliasToStaticType_StrictModeReturnsEmptyStub()
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

        Assert.Equal(string.Empty, stub);
    }

    [Fact]
    public void BuildStubDeclaration_LocalVariableTypeAliasType_UsesResolvedQualifiedTypeName()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "selectedType",
            expression: "db.Users.Where(u => selectedType != null).Select(u => u.Id)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["selectedType"] = "Type"
            },
            usingAliases: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Type"] = "System.Type"
            });

        Assert.Contains("(System.Type)", stub, StringComparison.Ordinal);
        Assert.DoesNotContain("(Type)", stub, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStubDeclaration_UnresolvedTypeMarkerQuestionMark_StrictModeReturnsEmptyStub()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "unknownType",
            expression: "db.Orders.Skip(pageSize)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["unknownType"] = "?"
            });

        Assert.Equal(string.Empty, stub);
    }

    [Fact]
    public async Task Evaluate_PagingWithMathCall_DoesNotSurfaceStaticTypeCompilationErrors()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id).Skip(Math.Max(pageSize * pageIndex, 0)).Take(pageSize).Select(expression)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pageSize"] = "int",
                ["pageIndex"] = "int",
                ["expression"] = "System.Linq.Expressions.Expression<System.Func<SampleMySqlApp.Domain.Entities.Order, object>>",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Cannot declare a variable of static type 'Math'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cannot convert to static type 'Math'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_PagingWithMathCall_WithStaticLspHint_StrictModeFailsWithoutGuesses()
    {
        var result = await _evaluator.EvaluateAsync(_alcCtx, new TranslationRequest
        {
            AssemblyPath = _alcCtx.AssemblyPath,
            Expression = "db.Orders.OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(expression)",
            LocalSymbolGraph =
            [
                new LocalSymbolGraphEntry
                {
                    Name = "page",
                    TypeName = "Math",
                    Kind = "local",
                    DeclarationOrder = 0,
                },
                new LocalSymbolGraphEntry
                {
                    Name = "pageSize",
                    TypeName = "Math",
                    Kind = "local",
                    DeclarationOrder = 1,
                },
            ],
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("CS0103", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The name 'page' does not exist", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingPagingVariables_InSkipTakeArithmetic_AreSynthesizedAsNumeric()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.OrderBy(o => o.Id).Skip(pageSize * pageIndex).Take(pageSize).Select(o => o.Id)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pageSize"] = "int",
                ["pageIndex"] = "int",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain(
            "Operator '*' cannot be applied to operands of type 'object' and 'object'",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_PatternTernaryComparison_UsingLspNormalizedForm_Translates()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.Where(o => o.UserId == (selector.Value == 1 || selector.Value == 2 ? 1 : 2)).Select(o => o.Id)",
            memberTypeHints:
            [
                new MemberTypeHint { ReceiverName = "selector", MemberName = "Value", TypeName = "int" },
            ],
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("CS0103", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("selector", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_PatternTernaryComparisonInsideLogicalAnd_UsingLspNormalizedForm_Translates()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.Where(o => o.Id > 0 && o.UserId == (selector.Value == 1 || selector.Value == 2 ? 1 : 2)).Select(o => o.Id)",
            memberTypeHints:
            [
                new MemberTypeHint { ReceiverName = "selector", MemberName = "Value", TypeName = "int" },
            ],
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("CS0103", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("selector", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WideProjection_DoesNotFailWithIndexOutOfRange()
    {
        var projectionMembers = string.Join(", ", Enumerable.Range(1, 64).Select(i => $"C{i} = u.Id"));
        var expression = $"db.Users.Select(u => new {{ {projectionMembers} }})";

        var result = await TranslateAsync(expression, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Index was outside the bounds of the array", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingCollectionVariable_InContains_DoesNotFallBackToObject()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.Where(o => userIds.Contains(o.UserId))",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["userIds"] = "System.Collections.Generic.List<int>",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("'object' does not contain a definition for 'Contains'",
            result.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingCollectionVariable_InContains_DoesNotCollapseToWhereFalse()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.Where(o => userIds.Contains(o.UserId))",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["userIds"] = "System.Collections.Generic.List<int>",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("WHERE FALSE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStubDeclaration_GlobalQualifiedCollectionElementType_UsesDeterministicQualifiedType()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "categoryList",
            expression: "db.Orders.Where(o => categoryList.Contains(System.DayOfWeek.Monday))",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["categoryList"] = "global::System.Collections.Generic.List<global::System.DayOfWeek>",
            });

        Assert.Equal(
            "System.Collections.Generic.List<System.DayOfWeek> categoryList = new() { default(System.DayOfWeek), default(System.DayOfWeek) };",
            stub);
    }

    [Fact]
    public void BuildStubDeclaration_ArrayVariable_UsesAtLeastTwoPlaceholderValues()
    {
        var stub = BuildStubDeclarationForRequestForTest(
            missingName: "ids",
            expression: "db.Orders.Where(o => ids.Contains(o.Id))",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ids"] = "System.Guid[]",
            });

        Assert.Equal(
            "System.Guid[] ids = new System.Guid[] { default(System.Guid), default(System.Guid) };",
            stub);
    }

    [Fact]
    public async Task Evaluate_MissingGuidCollectionVariable_InContains_UsesAtLeastTwoPlaceholderValues()
    {
        var result = await TranslateStrictAsync(
            "db.ApplicationChecklists.Where(c => listingIds.Contains(c.ApplicationId))",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["listingIds"] = "System.Collections.Generic.List<System.Guid>",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingSelectorVariable_InSelect_IsSynthesized()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.Select(selector)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["selector"] = "System.Linq.Expressions.Expression<System.Func<SampleMySqlApp.Domain.Entities.Order, object>>",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
    }

    [Fact]
    public async Task Evaluate_MissingWhereAndSelectExpressionVariables_AreSynthesized()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.Where(filter).Select(expression)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["filter"] = "System.Linq.Expressions.Expression<System.Func<SampleMySqlApp.Domain.Entities.Order, bool>>",
                ["expression"] = "System.Linq.Expressions.Expression<System.Func<SampleMySqlApp.Domain.Entities.Order, object>>",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingSelectExpression_WithSemanticCommentPreamble_DoesNotFallBackToObject()
    {
        var expression = """
            // var expression: Expression<Func<SampleMySqlApp.Domain.Entities.Order, TResult>> (used 8x)
            db.Orders
                .OrderByDescending(o => o.CreatedUtc)
                .ThenByDescending(o => o.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(expression)
            """;

        var result = await TranslateStrictAsync(
            expression,
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["page"] = "int",
                ["pageSize"] = "int",
                ["expression"] = "System.Linq.Expressions.Expression<System.Func<SampleMySqlApp.Domain.Entities.Order, object>>",
            },
            localSymbolHints:
            [
                new LocalSymbolHint { Name = "page", TypeName = "int", Kind = "local", InitializerExpression = "2" },
                new LocalSymbolHint { Name = "pageSize", TypeName = "int", Kind = "local", InitializerExpression = "25" },
            ],
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("CS0411", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WHERE FALSE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_PagingLocals_WithInitializerDependency_Order_IsResolvedBeforeCompilation()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(expression)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["expression"] = "System.Linq.Expressions.Expression<System.Func<SampleMySqlApp.Domain.Entities.Order, object>>",
            },
            localSymbolHints:
            [
                new LocalSymbolHint
                {
                    Name = "page",
                    TypeName = "int",
                    Kind = "local",
                    InitializerExpression = "Math.Max(request.Page, 1)",
                    DeclarationOrder = 1,
                },
                new LocalSymbolHint
                {
                    Name = "pageSize",
                    TypeName = "int",
                    Kind = "local",
                    InitializerExpression = "Math.Max(request.PageSize, 1)",
                    DeclarationOrder = 2,
                },
                // Intentionally placed last to mimic out-of-order extraction and ensure
                // dependency ordering handles "request" before page/pageSize declarations.
                new LocalSymbolHint
                {
                    Name = "request",
                    TypeName = "object",
                    Kind = "local",
                    InitializerExpression = "new { Page = 2, PageSize = 25 }",
                    DeclarationOrder = 0,
                },
            ],
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("CS0841", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WHERE FALSE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_MissingCancellationToken_InAsyncTerminal_IsSynthesized()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.SingleOrDefaultAsync(ct)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ct"] = "System.Threading.CancellationToken",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Compilation error", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_NonCtCancellationTokenName_InAsyncTerminal_IsSynthesized()
    {
        var result = await TranslateStrictAsync(
            "db.Orders.SingleOrDefaultAsync(cancellationToken)",
            localVariableTypes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cancellationToken"] = "System.Threading.CancellationToken",
            },
            useAsyncRunner: true,
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("CS0103", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
