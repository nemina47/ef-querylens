using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_LegacyRequest_UnaffectedByV2Gate()
    {
        var result = await _evaluator.EvaluateAsync(
            _alcCtx,
            new TranslationRequest
            {
                AssemblyPath = _alcCtx.AssemblyPath,
                Expression = "db.Orders",
                DbContextTypeName = DefaultMySqlDbContextType,
                AdditionalImports = [],
                UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = [],
                LocalSymbolGraph = [],
                UseAsyncRunner = false,
                // No v2 payloads: should transparently use legacy path.
                V2ExtractionPlan = null,
                V2CapturePlan = null,
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.NotEmpty(result.Sql);
    }

    [Fact]
    public async Task EvaluateAsync_V2RejectedCapture_ReturnsBlockedDiagnostic()
    {
        var result = await _evaluator.EvaluateAsync(
            _alcCtx,
            new TranslationRequest
            {
                AssemblyPath = _alcCtx.AssemblyPath,
                Expression = "db.Orders",
                DbContextTypeName = DefaultMySqlDbContextType,
                AdditionalImports = [],
                UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = [],
                LocalSymbolGraph = [],
                UseAsyncRunner = false,
                V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
                {
                    Expression = "db.Orders",
                    ContextVariableName = "db",
                    RootContextVariableName = "db",
                    RootMemberName = "Orders",
                    BoundaryKind = "Queryable",
                    NeedsMaterialization = false,
                },
                V2CapturePlan = new V2CapturePlanSnapshot
                {
                    ExecutableExpression = "db.Orders",
                    IsComplete = false,
                    Diagnostics =
                    [
                        new V2CaptureDiagnostic
                        {
                            Code = "MISSING_SYMBOL",
                            SymbolName = "tenantId",
                            Message = "Symbol 'tenantId' cannot be captured from outer scope.",
                        },
                    ],
                },
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("capture-rejected:", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_V2StringPlaceholder_DoesNotCollapseToConstantFalseSql()
    {
        const string expression =
            "db.Customers.Where(c => c.Name.ToLower().Contains(term) || c.Email.ToLower().StartsWith(term))";

        var result = await _evaluator.EvaluateAsync(
            _alcCtx,
            new TranslationRequest
            {
                AssemblyPath = _alcCtx.AssemblyPath,
                Expression = expression,
                DbContextTypeName = DefaultMySqlDbContextType,
                AdditionalImports = [],
                UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = [],
                LocalSymbolGraph = [],
                UseAsyncRunner = false,
                V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
                {
                    Expression = expression,
                    ContextVariableName = "db",
                    RootContextVariableName = "db",
                    RootMemberName = "Customers",
                    BoundaryKind = "Queryable",
                    NeedsMaterialization = false,
                },
                V2CapturePlan = new V2CapturePlanSnapshot
                {
                    ExecutableExpression = expression,
                    IsComplete = true,
                    Entries =
                    [
                        new V2CapturePlanEntry
                        {
                            Name = "term",
                            TypeName = "string",
                            Kind = "parameter",
                            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                            DeclarationOrder = 0,
                        },
                    ],
                    Diagnostics = [],
                },
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("0 = 1", result.Sql!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_V2ArrayPlaceholder_ContainsPredicate_ReturnsDeterministicFailure()
    {
        const string expression = "db.Customers.Where(c => termsArray.Contains(c.Name))";

        var result = await _evaluator.EvaluateAsync(
            _alcCtx,
            new TranslationRequest
            {
                AssemblyPath = _alcCtx.AssemblyPath,
                Expression = expression,
                DbContextTypeName = DefaultMySqlDbContextType,
                AdditionalImports = [],
                UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = [],
                LocalSymbolGraph = [],
                UseAsyncRunner = false,
                V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
                {
                    Expression = expression,
                    ContextVariableName = "db",
                    RootContextVariableName = "db",
                    RootMemberName = "Customers",
                    BoundaryKind = "Queryable",
                    NeedsMaterialization = false,
                },
                V2CapturePlan = new V2CapturePlanSnapshot
                {
                    ExecutableExpression = expression,
                    IsComplete = true,
                    Entries =
                    [
                        new V2CapturePlanEntry
                        {
                            Name = "termsArray",
                            TypeName = "string[]",
                            Kind = "parameter",
                            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                            DeclarationOrder = 0,
                        },
                    ],
                    Diagnostics = [],
                },
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains(
            "attempting to evaluate a LINQ query parameter expression",
            result.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateAsync_V2ListPlaceholder_ContainsPredicate_TranslatesWithStableSqlShape()
    {
        const string expression = "db.Customers.Where(c => termsList.Contains(c.Name))";

        var result = await _evaluator.EvaluateAsync(
            _alcCtx,
            new TranslationRequest
            {
                AssemblyPath = _alcCtx.AssemblyPath,
                Expression = expression,
                DbContextTypeName = DefaultMySqlDbContextType,
                AdditionalImports = [],
                UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = [],
                LocalSymbolGraph = [],
                UseAsyncRunner = false,
                V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
                {
                    Expression = expression,
                    ContextVariableName = "db",
                    RootContextVariableName = "db",
                    RootMemberName = "Customers",
                    BoundaryKind = "Queryable",
                    NeedsMaterialization = false,
                },
                V2CapturePlan = new V2CapturePlanSnapshot
                {
                    ExecutableExpression = expression,
                    IsComplete = true,
                    Entries =
                    [
                        new V2CapturePlanEntry
                        {
                            Name = "termsList",
                            TypeName = "List<string>",
                            Kind = "parameter",
                            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                            DeclarationOrder = 0,
                        },
                    ],
                    Diagnostics = [],
                },
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("IN", result.Sql!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0 = 1", result.Sql!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_V2EnumerablePlaceholder_ContainsPredicate_ReturnsDeterministicFailure()
    {
        const string expression = "db.Customers.Where(c => termsEnumerable.Contains(c.Name))";

        var result = await _evaluator.EvaluateAsync(
            _alcCtx,
            new TranslationRequest
            {
                AssemblyPath = _alcCtx.AssemblyPath,
                Expression = expression,
                DbContextTypeName = DefaultMySqlDbContextType,
                AdditionalImports = [],
                UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = [],
                LocalSymbolGraph = [],
                UseAsyncRunner = false,
                V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
                {
                    Expression = expression,
                    ContextVariableName = "db",
                    RootContextVariableName = "db",
                    RootMemberName = "Customers",
                    BoundaryKind = "Queryable",
                    NeedsMaterialization = false,
                },
                V2CapturePlan = new V2CapturePlanSnapshot
                {
                    ExecutableExpression = expression,
                    IsComplete = true,
                    Entries =
                    [
                        new V2CapturePlanEntry
                        {
                            Name = "termsEnumerable",
                            TypeName = "IEnumerable<string>",
                            Kind = "parameter",
                            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                            DeclarationOrder = 0,
                        },
                    ],
                    Diagnostics = [],
                },
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains(
            "attempting to evaluate a LINQ query parameter expression",
            result.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateAsync_V2ReadOnlyCollectionPlaceholder_ContainsPredicate_TranslatesWithStableSqlShape()
    {
        const string expression = "db.Customers.Where(c => termsCollection.Contains(c.Name))";

        var result = await _evaluator.EvaluateAsync(
            _alcCtx,
            new TranslationRequest
            {
                AssemblyPath = _alcCtx.AssemblyPath,
                Expression = expression,
                DbContextTypeName = DefaultMySqlDbContextType,
                AdditionalImports = [],
                UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = [],
                LocalSymbolGraph = [],
                UseAsyncRunner = false,
                V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
                {
                    Expression = expression,
                    ContextVariableName = "db",
                    RootContextVariableName = "db",
                    RootMemberName = "Customers",
                    BoundaryKind = "Queryable",
                    NeedsMaterialization = false,
                },
                V2CapturePlan = new V2CapturePlanSnapshot
                {
                    ExecutableExpression = expression,
                    IsComplete = true,
                    Entries =
                    [
                        new V2CapturePlanEntry
                        {
                            Name = "termsCollection",
                            TypeName = "IReadOnlyCollection<string>",
                            Kind = "parameter",
                            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                            DeclarationOrder = 0,
                        },
                    ],
                    Diagnostics = [],
                },
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("IN", result.Sql!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0 = 1", result.Sql!, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE FALSE", result.Sql!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateAsync_V2ReadOnlyEnumCollectionPlaceholder_ContainsPredicate_TranslatesWithStableSqlShape()
    {
        const string expression = "db.Orders.Where(o => statuses.Contains(o.Status))";

        var result = await _evaluator.EvaluateAsync(
            _alcCtx,
            new TranslationRequest
            {
                AssemblyPath = _alcCtx.AssemblyPath,
                Expression = expression,
                DbContextTypeName = DefaultMySqlDbContextType,
                AdditionalImports = [],
                UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = [],
                LocalSymbolGraph = [],
                UseAsyncRunner = false,
                V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
                {
                    Expression = expression,
                    ContextVariableName = "db",
                    RootContextVariableName = "db",
                    RootMemberName = "Orders",
                    BoundaryKind = "Queryable",
                    NeedsMaterialization = false,
                },
                V2CapturePlan = new V2CapturePlanSnapshot
                {
                    ExecutableExpression = expression,
                    IsComplete = true,
                    Entries =
                    [
                        new V2CapturePlanEntry
                        {
                            Name = "statuses",
                            TypeName = "IReadOnlyCollection<SampleMySqlApp.Domain.Enums.OrderStatus>",
                            Kind = "parameter",
                            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                            DeclarationOrder = 0,
                        },
                    ],
                    Diagnostics = [],
                },
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("IN", result.Sql!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0 = 1", result.Sql!, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE FALSE", result.Sql!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateAsync_V2WhereAndSelectExpressionPlaceholders_DoNotProduceNullPredicate()
    {
        const string expression = "db.ApplicationChecklists.AsNoTracking().Where(filter).Select(select).ToListAsync(ct)";

        var result = await _evaluator.EvaluateAsync(
            _alcCtx,
            new TranslationRequest
            {
                AssemblyPath = _alcCtx.AssemblyPath,
                Expression = expression,
                DbContextTypeName = DefaultMySqlDbContextType,
                AdditionalImports = [],
                UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = [],
                LocalSymbolGraph = [],
                UseAsyncRunner = false,
                V2ExtractionPlan = new V2QueryExtractionPlanSnapshot
                {
                    Expression = expression,
                    ContextVariableName = "db",
                    RootContextVariableName = "db",
                    RootMemberName = "ApplicationChecklists",
                    BoundaryKind = "Materialized",
                    NeedsMaterialization = false,
                },
                V2CapturePlan = new V2CapturePlanSnapshot
                {
                    ExecutableExpression = expression,
                    IsComplete = true,
                    Entries =
                    [
                        new V2CapturePlanEntry
                        {
                            Name = "filter",
                            TypeName = "global::System.Linq.Expressions.Expression<global::System.Func<global::SampleMySqlApp.Domain.Entities.ApplicationChecklist, bool>>",
                            Kind = "parameter",
                            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                            DeclarationOrder = 0,
                        },
                        new V2CapturePlanEntry
                        {
                            Name = "select",
                            TypeName = "global::System.Linq.Expressions.Expression<global::System.Func<global::SampleMySqlApp.Domain.Entities.ApplicationChecklist, object>>",
                            Kind = "parameter",
                            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                            DeclarationOrder = 1,
                        },
                        new V2CapturePlanEntry
                        {
                            Name = "ct",
                            TypeName = "System.Threading.CancellationToken",
                            Kind = "parameter",
                            CapturePolicy = LocalSymbolReplayPolicies.UsePlaceholder,
                            DeclarationOrder = 2,
                        },
                    ],
                    Diagnostics = [],
                },
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("Value cannot be null. (Parameter 'predicate')", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
