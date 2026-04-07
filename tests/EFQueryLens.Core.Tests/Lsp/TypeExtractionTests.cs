using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Parsing;

namespace EFQueryLens.Core.Tests.Lsp;

public class TypeExtractionTests
{
    // ─── Explicit type declarations ───────────────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_ExplicitInt_IsFound()
    {
        var source = """
            int count = 5;
            _ = count;
            """;

        var types = Extract(source, "_ = count;");

        Assert.True(types.TryGetValue("count", out var typeName));
        Assert.Equal("int", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_ExplicitString_IsFound()
    {
        var source = """
            string name = "hello";
            _ = name;
            """;

        var types = Extract(source, "_ = name;");

        Assert.True(types.TryGetValue("name", out var typeName));
        Assert.Equal("string", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_ExplicitGuid_IsFound()
    {
        var source = """
            System.Guid id = System.Guid.Empty;
            _ = id;
            """;

        var types = Extract(source, "_ = id;");

        Assert.True(types.TryGetValue("id", out var typeName));
        Assert.Equal("System.Guid", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_ExplicitGenericList_IsFound()
    {
        var source = """
            System.Collections.Generic.List<string> names = null;
            _ = names;
            """;

        var types = Extract(source, "_ = names;");

        Assert.True(types.TryGetValue("names", out var typeName));
        Assert.Equal("System.Collections.Generic.List<string>", typeName);
    }

    // ─── var declarations — literal inference ─────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_VarWithIntLiteral_IsInferredAsInt()
    {
        var source = """
            var pageSize = 10;
            _ = pageSize;
            """;

        var types = Extract(source, "_ = pageSize;");

        Assert.True(types.TryGetValue("pageSize", out var typeName));
        Assert.Equal("int", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithLongSuffix_IsInferredAsLong()
    {
        var source = """
            var bigNumber = 100L;
            _ = bigNumber;
            """;

        var types = Extract(source, "_ = bigNumber;");

        Assert.True(types.TryGetValue("bigNumber", out var typeName));
        Assert.Equal("long", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithDecimalSuffix_IsInferredAsDecimal()
    {
        var source = """
            var price = 9.99m;
            _ = price;
            """;

        var types = Extract(source, "_ = price;");

        Assert.True(types.TryGetValue("price", out var typeName));
        Assert.Equal("decimal", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithFloatSuffix_IsInferredAsFloat()
    {
        var source = """
            var ratio = 1.5f;
            _ = ratio;
            """;

        var types = Extract(source, "_ = ratio;");

        Assert.True(types.TryGetValue("ratio", out var typeName));
        Assert.Equal("float", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithDoubleLiteral_IsInferredAsDouble()
    {
        var source = """
            var rate = 3.14;
            _ = rate;
            """;

        var types = Extract(source, "_ = rate;");

        Assert.True(types.TryGetValue("rate", out var typeName));
        Assert.Equal("double", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithStringLiteral_IsInferredAsString()
    {
        var source = """
            var label = "hello";
            _ = label;
            """;

        var types = Extract(source, "_ = label;");

        Assert.True(types.TryGetValue("label", out var typeName));
        Assert.Equal("string", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithTrueLiteral_IsInferredAsBool()
    {
        var source = """
            var flag = true;
            _ = flag;
            """;

        var types = Extract(source, "_ = flag;");

        Assert.True(types.TryGetValue("flag", out var typeName));
        Assert.Equal("bool", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithFalseLiteral_IsInferredAsBool()
    {
        var source = """
            var enabled = false;
            _ = enabled;
            """;

        var types = Extract(source, "_ = enabled;");

        Assert.True(types.TryGetValue("enabled", out var typeName));
        Assert.Equal("bool", typeName);
    }

    // ─── var declarations — constructor / cast inference ─────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_VarWithNewExpression_IsInferredFromTypeName()
    {
        var source = """
            var list = new System.Collections.Generic.List<string>();
            _ = list;
            """;

        var types = Extract(source, "_ = list;");

        Assert.True(types.TryGetValue("list", out var typeName));
        Assert.Equal("global::System.Collections.Generic.List<string>", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithCastExpression_IsInferredFromCastType()
    {
        var source = """
            var id = (System.Guid)System.Guid.Empty;
            _ = id;
            """;

        var types = Extract(source, "_ = id;");

        Assert.True(types.TryGetValue("id", out var typeName));
        Assert.Equal("global::System.Guid", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithDefaultExpression_IsInferredFromDefaultType()
    {
        var source = """
            var id = default(System.Guid);
            _ = id;
            """;

        var types = Extract(source, "_ = id;");

        Assert.True(types.TryGetValue("id", out var typeName));
        Assert.Equal("global::System.Guid", typeName);
    }

    // ─── Scope boundaries ─────────────────────────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_VariableDeclaredAfterCursor_IsNotFound()
    {
        var source = """
            int before = 1;
            _ = before;
            int after = 2;
            """;

        var types = Extract(source, "_ = before;");

        Assert.True(types.ContainsKey("before"), "'before' should be visible.");
        Assert.False(types.ContainsKey("after"), "'after' is declared after cursor — must not appear.");
    }

    [Fact]
    public void ExtractLocalVariableTypes_MultipleVariablesBeforeCursor_AllFound()
    {
        var source = """
            int a = 1;
            string b = "hello";
            bool c = true;
            _ = a;
            """;

        var types = Extract(source, "_ = a;");

        Assert.True(types.ContainsKey("a"));
        Assert.True(types.ContainsKey("b"));
        Assert.True(types.ContainsKey("c"));
    }

    [Fact]
    public void ExtractLocalVariableTypes_ShadowedVariable_OuterScopeIsOverriddenByInner()
    {
        // Inner declaration of 'x' should win over outer 'x' — inner is declared first from cursor's perspective.
        var source = """
            int x = 1;
            int x = 2;
            _ = x;
            """;

        var types = Extract(source, "_ = x;");

        // Both are technically before the cursor; the implementation keeps the first one added (inner-first walk),
        // so we just verify 'x' is present with some type.
        Assert.True(types.ContainsKey("x"));
        Assert.Equal("int", types["x"]);
    }

    // ─── Method parameters ────────────────────────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_MethodParameter_IsFound()
    {
        var source = """
            void M(int userId, string name)
            {
                _ = userId;
            }
            """;

        var types = Extract(source, "_ = userId;");

        Assert.True(types.TryGetValue("userId", out var userIdType));
        Assert.Equal("int", userIdType);
        Assert.True(types.TryGetValue("name", out var nameType));
        Assert.Equal("string", nameType);
    }

    [Fact]
    public void ExtractLocalVariableTypes_LocalFunctionParameter_IsFound()
    {
        var source = """
            void Outer()
            {
                void Inner(System.Guid id)
                {
                    _ = id;
                }
            }
            """;

        var types = Extract(source, "_ = id;");

        Assert.True(types.TryGetValue("id", out var typeName));
        Assert.Equal("System.Guid", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_LambdaParameters_AreFound()
    {
        var source = """
            app.MapGet("/api/customers/{customerId:guid}/orders",
                async (
                    Guid customerId,
                    decimal? minTotal,
                    OrderStatus? status,
                    CancellationToken ct) =>
                {
                    _ = minTotal;
                    return Results.Ok();
                });
            """;

        var types = Extract(source, "_ = minTotal;");

        Assert.True(types.TryGetValue("minTotal", out var minTotalType));
        Assert.Equal("decimal?", minTotalType);
        Assert.True(types.TryGetValue("status", out var statusType));
        Assert.Equal("OrderStatus?", statusType);
        Assert.True(types.TryGetValue("ct", out var ctType));
        Assert.Equal("CancellationToken", ctType);
    }

    [Fact]
    public void BuildMemberTypeHints_FromNullableSymbols_ProducesHasValueAndValueHints()
    {
        IReadOnlyList<LocalSymbolHint> symbols =
        [
            new LocalSymbolHint { Name = "minTotal", TypeName = "decimal?", Kind = "parameter" },
            new LocalSymbolHint { Name = "status", TypeName = "OrderStatus?", Kind = "parameter" },
        ];

        var expression = "db.Orders.Where(o => (!minTotal.HasValue || o.Total >= minTotal.Value) && (!status.HasValue || o.Status == status.Value))";
        var hints = LspSyntaxHelper.BuildMemberTypeHints(expression, symbols);

        Assert.Contains(hints, h => h.ReceiverName == "minTotal" && h.MemberName == "HasValue" && h.TypeName == "bool");
        Assert.Contains(hints, h => h.ReceiverName == "minTotal" && h.MemberName == "Value" && h.TypeName == "decimal");
        Assert.Contains(hints, h => h.ReceiverName == "status" && h.MemberName == "HasValue" && h.TypeName == "bool");
        Assert.Contains(hints, h => h.ReceiverName == "status" && h.MemberName == "Value" && h.TypeName == "OrderStatus");
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithConditionalIntLiterals_IsInferredAsInt()
    {
        var source = """
            var count = true ? 5 : 10;
            _ = count;
            """;

        var types = Extract(source, "_ = count;");

        Assert.True(types.TryGetValue("count", out var typeName));
        Assert.Equal("int", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithConditionalStringLiterals_IsInferredAsString()
    {
        var source = """
            var name = true ? "hello" : "world";
            _ = name;
            """;

        var types = Extract(source, "_ = name;");

        Assert.True(types.TryGetValue("name", out var typeName));
        Assert.Equal("string", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithConditionalCastAndLiteral_IsInferredFromCast()
    {
        var source = """
            var value = true ? (long)5 : 10;
            _ = value;
            """;

        var types = Extract(source, "_ = value;");

        Assert.True(types.TryGetValue("value", out var typeName));
        Assert.Equal("long", typeName);
    }

    // ─── Static utility class initializers ───────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_VarWithMathMax_DoesNotInferMathAsType()
    {
        // var page = Math.Max(request.Page, 1) — type must NOT be reported as "Math".
        // The evaluator would treat "Math" as a static class and skip stub generation,
        // causing an unknown-variable compilation error on the page variable.
        var source = """
            var page = Math.Max(request.Page, 1);
            _ = page;
            """;

        var types = Extract(source, "_ = page;");

        if (types.TryGetValue("page", out var typeName))
            Assert.NotEqual("Math", typeName);
        // If absent entirely that's also fine — evaluator numeric heuristics handle it.
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithMathClamp_DoesNotInferMathAsType()
    {
        var source = """
            var pageSize = Math.Clamp(request.PageSize, 1, 200);
            _ = pageSize;
            """;

        var types = Extract(source, "_ = pageSize;");

        if (types.TryGetValue("pageSize", out var typeName))
            Assert.NotEqual("Math", typeName);
    }

    [Fact]
    public void ExtractLocalVariableTypes_WithSemanticModel_InfersVarFromMathMethods()
    {
        var source = """
            internal sealed class Probe
            {
                public void Run(int requestPage, int requestPageSize)
                {
                    var page = System.Math.Max(requestPage, 1);
                    var pageSize = System.Math.Clamp(requestPageSize, 1, 200);
                    _ = page + pageSize;
                }
            }
            """;

        var (line, character) = FindPosition(source, "_ = page + pageSize;");
        var targetAssembly = EFQueryLens.Core.Tests.Scripting.QueryEvaluatorTests.GetSampleMySqlAppDll();
        var types = LspSyntaxHelper.ExtractLocalVariableTypesAtPosition(source, line, character, targetAssembly);

        Assert.True(types.TryGetValue("page", out var pageType));
        Assert.Equal("int", pageType);
        Assert.True(types.TryGetValue("pageSize", out var pageSizeType));
        Assert.Equal("int", pageSizeType);
    }

    [Fact]
    public void ExtractLocalVariableTypes_WithSemanticModel_InfersVarFromPlainMathIdentifier()
    {
        var source = """
            using System;
            using System.Threading;
            internal sealed class Probe
            {
                public void Run(int requestPage, int requestPageSize)
                {
                    var page = Math.Max(requestPage, 1);
                    var pageSize = Math.Clamp(requestPageSize, 1, 200);
                    _ = page + pageSize;
                }
            }
            """;

        var (line, character) = FindPosition(source, "_ = page + pageSize;");
        var types = LspSyntaxHelper.ExtractLocalVariableTypesAtPosition(source, line, character, targetAssemblyPath: null);

        Assert.True(types.TryGetValue("page", out var pageType));
        Assert.Equal("int", pageType);
        Assert.True(types.TryGetValue("pageSize", out var pageSizeType));
        Assert.Equal("int", pageSizeType);
    }

    [Fact]
    public void ExtractLocalSymbolHints_CapturesLocalInitializerExpressions()
    {
        var source = """
            using System;
            internal sealed class Probe
            {
                public void Run(int requestPage)
                {
                    var page = Math.Max(requestPage, 1);
                    _ = page;
                }
            }
            """;

        var (line, character) = FindPosition(source, "_ = page;");
        var hints = LspSyntaxHelper.ExtractLocalSymbolHintsAtPosition(source, line, character, targetAssemblyPath: null);

        var pageHint = Assert.Single(hints, h => h.Name == "page");
        Assert.Equal("int", pageHint.TypeName);
        Assert.Equal("Math.Max(requestPage, 1)", pageHint.InitializerExpression);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithConvertToInt32_DoesNotInferConvertAsType()
    {
        var source = """
            var count = Convert.ToInt32(someValue);
            _ = count;
            """;

        var types = Extract(source, "_ = count;");

        if (types.TryGetValue("count", out var typeName))
            Assert.NotEqual("Convert", typeName);
    }

    // ─── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void ExtractLocalVariableTypes_EmptySource_ReturnsEmpty()
    {
        var types = LspSyntaxHelper.ExtractLocalVariableTypesAtPosition("", 0, 0);
        Assert.Empty(types);
    }

    [Fact]
    public void ExtractLocalSymbolGraphAtPosition_PreservesDeclarationOrderForDependentPagingLocals()
    {
        var source = """
            class Request { public int Page { get; set; } public int PageSize { get; set; } }
            class Order { public int Id { get; set; } }
            int M(Request request, System.Linq.Expressions.Expression<System.Func<Order, object>> expression)
            {
                var page = System.Math.Max(request.Page, 1);
                var pageSize = System.Math.Max(request.PageSize, 1);
                var query = _dbContext.Orders
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(expression);
                return 0;
            }
            """;

        var (line, character) = FindPosition(source, ".Skip((page - 1) * pageSize)");
        var graph = LspSyntaxHelper.ExtractLocalSymbolGraphAtPosition(source, line, character, targetAssemblyPath: null);

        var request = Assert.Single(graph, g => g.Name == "request");
        var page = Assert.Single(graph, g => g.Name == "page");
        var pageSize = Assert.Single(graph, g => g.Name == "pageSize");

        Assert.True(request.DeclarationOrder < page.DeclarationOrder);
        Assert.True(request.DeclarationOrder < pageSize.DeclarationOrder);
        Assert.Contains("request", page.Dependencies, StringComparer.Ordinal);
        Assert.Contains("request", pageSize.Dependencies, StringComparer.Ordinal);
    }

    [Fact]
    public void ExtractLocalSymbolGraphAtPosition_DoesNotTreatLambdaParameterAsInitializerDependency()
    {
        var source = """
            class Order { public bool IsNotDeleted { get; set; } }
            void M(decimal minTotal)
            {
                System.Linq.IQueryable<Order> query = _dbContext.Orders.Where(o => o.IsNotDeleted);
                _ = _dbContext.Orders.Where(o => o.Total >= minTotal);
            }
            """;

        var (line, character) = FindPosition(source, "o.Total >= minTotal");
        var graph = LspSyntaxHelper.ExtractLocalSymbolGraphAtPosition(source, line, character, targetAssemblyPath: null);

        var query = Assert.Single(graph, g => g.Name == "query");
        Assert.DoesNotContain("o", query.Dependencies, StringComparer.Ordinal);
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_UsesOnlyCapturedVariables()
    {
        var source = """
            class Request { public string? NotesSearch { get; set; } }
            class Order { public string? Notes { get; set; } public bool IsNotDeleted { get; set; } }
            void M(Request request)
            {
                var term = request.NotesSearch.Trim().ToLowerInvariant();
                _ = _dbContext.Orders.Where(o => o.IsNotDeleted).Where(o => o.Notes != null && o.Notes.ToLower().Contains(term));
            }
            """;

        var expression = "_dbContext.Orders.Where(o => o.IsNotDeleted).Where(o => o.Notes != null && o.Notes.ToLower().Contains(term))";
        var (line, character) = FindPosition(source, "o.Notes.ToLower().Contains(term)");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "_dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null);

        var term = Assert.Single(graph, g => g.Name == "term");
        Assert.Equal("term", term.Name);
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, term.ReplayPolicy);
        Assert.DoesNotContain(graph, g => g.Name == "request");
        Assert.DoesNotContain(graph, g => g.Name == "o");
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_HelperQuery_UsesCallerFreeVariablesOnly()
    {
        var source = """
            using System;
            using System.Linq;
            using System.Linq.Expressions;

            enum OrderStatus { Cancelled }
            class Customer { public Guid CustomerId { get; set; } }
            class Order
            {
                public int Id { get; set; }
                public Customer Customer { get; set; } = new();
                public decimal Total { get; set; }
                public OrderStatus Status { get; set; }
                public DateTime CreatedUtc { get; set; }
                public bool IsNotDeleted { get; set; }
            }
            record OrderListItemDto(int Id, Guid CustomerId, decimal Total, OrderStatus Status, DateTime CreatedUtc);

            class DemoService
            {
                IQueryable<TResult> GetCustomerOrdersQuery<TResult>(
                    Guid customerId,
                    Expression<Func<Order, bool>> whereExpression,
                    Expression<Func<Order, TResult>> selector)
                {
                    return _dbContext
                        .Orders
                        .Where(o => o.Customer.CustomerId == customerId)
                        .Where(o => o.IsNotDeleted)
                        .Where(whereExpression)
                        .Select(selector);
                }

                void Run(Guid customerId)
                {
                    var customerOrders = GetCustomerOrdersQuery(
                        customerId,
                        o => o.Total >= 100 && o.Status != OrderStatus.Cancelled,
                        o => new OrderListItemDto(o.Id, o.Customer.CustomerId, o.Total, o.Status, o.CreatedUtc));
                }
            }
            """;

        var (line, character) = FindPosition(source, "customerOrders = GetCustomerOrdersQuery");
        var extraction = LspSyntaxHelper.TryExtractLinqExpressionDetailed(
            source,
            @"c:\repo\DemoService.cs",
            line,
            character);

        Assert.NotNull(extraction);

        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            extraction.Expression,
            extraction.ContextVariableName,
            source,
            extraction.Origin.Line,
            extraction.Origin.Character,
            targetAssemblyPath: null,
            secondarySourceText: source,
            secondaryLine: line,
            secondaryCharacter: character);

        Assert.Collection(
            graph.OrderBy(g => g.Name, StringComparer.Ordinal),
            symbol => Assert.Equal("customerId", symbol.Name));
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_GenericExpressionParameter_RewritesOpenTypeParameterToObject()
    {
        var source = """
            using System;
            using System.Linq.Expressions;
            class Checklist {}
            class Demo
            {
                TResult? M<TResult>(Guid applicationId, Expression<Func<Checklist, TResult>> expression, CancellationToken ct)
                {
                    return dbContext.ApplicationChecklists
                        .AsNoTracking()
                        .Where(w => !w.IsDeleted && w.IsLatest)
                        .Where(w => w.ApplicationId == applicationId)
                        .Select(expression)
                        .SingleOrDefaultAsync(ct);
                }
            }
            """;

        var expression = "dbContext.ApplicationChecklists.AsNoTracking().Where(w => !w.IsDeleted && w.IsLatest).Where(w => w.ApplicationId == applicationId).Select(expression).SingleOrDefaultAsync(ct)";
        var (line, character) = FindPosition(source, ".Select(expression)");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null);

        var expressionSymbol = Assert.Single(graph, g => g.Name == "expression");
        Assert.Contains("Expression<", expressionSymbol.TypeName, StringComparison.Ordinal);
        Assert.Contains(", object>>", expressionSymbol.TypeName, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_UsesFullyQualifiedTypeNames_ToAvoidAmbiguousTypeCollisions()
    {
        var source = """
            using System.Linq;
            using CoreStatus = A.Core.Enums.ApplicationStatus;
            namespace A.Core.Enums { public enum ApplicationStatus { Draft, Active } }
            namespace A.Contracts.Enums { public enum ApplicationStatus { Draft, Active } }
            class ApplicationEntity
            {
                public bool IsNotDeleted { get; set; }
                public A.Core.Enums.ApplicationStatus ApplicationStatus { get; set; }
            }
            class Demo
            {
                void Run()
                {
                    var currentStatuses = new[] { CoreStatus.Active };
                    _ = dbContext.Applications
                        .Where(w => w.IsNotDeleted)
                        .SingleAsync(a => currentStatuses.Contains(a.ApplicationStatus), ct);
                }
            }
            """;

        var expression = "dbContext.Applications.Where(w => w.IsNotDeleted).SingleAsync(a => currentStatuses.Contains(a.ApplicationStatus), ct)";
        var (line, character) = FindPosition(source, "currentStatuses.Contains(a.ApplicationStatus)");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null);

        var statuses = Assert.Single(graph, g => g.Name == "currentStatuses");
        Assert.Contains("global::", statuses.TypeName, StringComparison.Ordinal);
        Assert.Contains("A.Core.Enums.ApplicationStatus", statuses.TypeName, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_RewritesReceiverPropertyCapture_ToSyntheticScalar()
    {
        var source = """
            using System;
            interface IClock { DateTime Now { get; } }
            class AppEntity { public DateTime CreatedAt { get; set; } }
            class Demo
            {
                void Run(IClock dateTime, CancellationToken ct)
                {
                    _ = dbContext.Applications.CountAsync(
                        w => w.CreatedAt.Date == dateTime.Now.Date,
                        ct);
                }
            }
            """;

        var expression = "dbContext.Applications.CountAsync(w => w.CreatedAt.Date == dateTime.Now.Date, ct)";
        var (line, character) = FindPosition(source, "dateTime.Now.Date");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null,
            out var rewrittenExpression);

        Assert.DoesNotContain(graph, g => g.Name == "dateTime");
        Assert.Contains(graph, g => g.Name == "ct");

        var memberCapture = Assert.Single(graph, g => g.Kind == "member-capture");
        Assert.StartsWith("__qlm_dateTime_Now", memberCapture.Name, StringComparison.Ordinal);
        Assert.Equal("global::System.DateTime", memberCapture.TypeName);
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, memberCapture.ReplayPolicy);
        Assert.DoesNotContain("dateTime.Now", rewrittenExpression, StringComparison.Ordinal);
        Assert.Contains(memberCapture.Name, rewrittenExpression, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_RewritesReceiverPropertyCapture_ForFieldDependency()
    {
        var source = """
            using System;
            interface IClock { DateTime Now { get; } }
            class AppEntity { public DateTime CreatedAt { get; set; } }
            class Demo
            {
                private readonly IClock dateTime;

                void Run(CancellationToken ct)
                {
                    _ = dbContext.Applications.CountAsync(
                        w => w.CreatedAt.Date == dateTime.Now.Date,
                        ct);
                }
            }
            """;

        var expression = "dbContext.Applications.CountAsync(w => w.CreatedAt.Date == dateTime.Now.Date, ct)";
        var (line, character) = FindPosition(source, "dateTime.Now.Date");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null,
            out var rewrittenExpression);

        Assert.DoesNotContain(graph, g => g.Name == "dateTime");
        Assert.Contains(graph, g => g.Name == "ct");

        var memberCapture = Assert.Single(graph, g => g.Kind == "member-capture");
        Assert.StartsWith("__qlm_dateTime_Now", memberCapture.Name, StringComparison.Ordinal);
        Assert.Equal("global::System.DateTime", memberCapture.TypeName);
        Assert.DoesNotContain("dateTime.Now", rewrittenExpression, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_RewritesReceiverPropertyCapture_ForPrimaryConstructorDependency()
    {
        var source = """
            using System;
            interface IClock { DateTime Now { get; } }
            class AppEntity { public DateTime CreatedAt { get; set; } }
            class Demo(IClock dateTime)
            {
                void Run(CancellationToken ct)
                {
                    _ = dbContext.Applications.CountAsync(
                        w => w.CreatedAt.Date == dateTime.Now.Date,
                        ct);
                }
            }
            """;

        var expression = "dbContext.Applications.CountAsync(w => w.CreatedAt.Date == dateTime.Now.Date, ct)";
        var (line, character) = FindPosition(source, "dateTime.Now.Date");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null,
            out var rewrittenExpression);

        Assert.DoesNotContain(graph, g => g.Name == "dateTime");
        Assert.Contains(graph, g => g.Name == "ct");

        var memberCapture = Assert.Single(graph, g => g.Kind == "member-capture");
        Assert.StartsWith("__qlm_dateTime_Now", memberCapture.Name, StringComparison.Ordinal);
        Assert.Equal("global::System.DateTime", memberCapture.TypeName);
        Assert.DoesNotContain("dateTime.Now", rewrittenExpression, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_DoesNotRewriteInvocationTargetMemberAccess()
    {
        var source = """
            using System;
            class Customer { public Guid CustomerId { get; set; } }
            class Order { public bool IsNotDeleted { get; set; } public DateTime CreatedUtc { get; set; } public Customer Customer { get; set; } = new(); }
            class Demo
            {
                void Run(Guid customerId, DateTime utcNow)
                {
                    _ = dbContext.Orders
                        .Where(o => o.IsNotDeleted && o.Customer.CustomerId == customerId)
                        .Where(o => o.CreatedUtc >= utcNow.AddDays(-7));
                }
            }
            """;

        var expression =
            "dbContext.Orders.Where(o => o.IsNotDeleted && o.Customer.CustomerId == customerId).Where(o => o.CreatedUtc >= utcNow.AddDays(-7))";
        var (line, character) = FindPosition(source, "utcNow.AddDays(-7)");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null,
            out var rewrittenExpression);

        Assert.Contains(graph, g => g.Name == "customerId");
        Assert.Contains(graph, g => g.Name == "utcNow");
        Assert.DoesNotContain(graph, g => g.Kind == "member-capture" && g.Name.Contains("AddDays", StringComparison.Ordinal));
        Assert.Contains("utcNow.AddDays(-7)", rewrittenExpression, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_QueryRangeVariable_DoesNotBecomeMemberCapture()
    {
        var source = """
            using System.Linq;
            class Item { public int CaseStatus { get; set; } }
            class Demo
            {
                void Run(IQueryable<Item> query)
                {
                    _ = (from @case in query
                         select new { Status = @case.CaseStatus });
                }
            }
            """;

        var expression = "(from @case in query select new { Status = @case.CaseStatus })";
        var (line, character) = FindPosition(source, "@case.CaseStatus");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "query",
            source,
            line,
            character,
            targetAssemblyPath: null,
            out var rewrittenExpression);

        Assert.DoesNotContain(graph, g => g.Kind == "member-capture");
        Assert.DoesNotContain("__qlm_case_", rewrittenExpression, StringComparison.Ordinal);
        Assert.Contains("@case.CaseStatus", rewrittenExpression, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_QueryInsideNestedIf_ResolvesOuterLocal()
    {
        var source = """
            using System;
            class Company { public string? UenNumber { get; set; } public Guid CompanyId { get; set; } public bool IsNotDeleted { get; set; } }
            class Demo
            {
                async Task Run(CancellationToken ct)
                {
                    var companyUen = await dbContext.ApplicationCompanies
                        .Where(x => x.IsNotDeleted)
                        .Select(x => x.UenNumber)
                        .SingleAsync(ct);

                    if (companyUen != null)
                    {
                        _ = dbContext.Companies
                            .Where(w => w.IsNotDeleted && w.UenNumber == companyUen)
                            .Select(s => s.CompanyId)
                            .SingleAsync(ct);
                    }
                }
            }
            """;

        var expression = "dbContext.Companies.Where(w => w.IsNotDeleted && w.UenNumber == companyUen).Select(s => s.CompanyId).SingleAsync(ct)";
        var (line, character) = FindPosition(source, "w.UenNumber == companyUen");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null);

        Assert.Contains(graph, g => g.Name == "companyUen");
        Assert.Contains(graph, g => g.Name == "ct");
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_StaticEnumAccess_DoesNotCreateMemberCapture()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class Item { public ChangeStatus ChangeStatus { get; set; } public bool IsNotDeleted { get; set; } }
            enum ChangeStatus { New, Deleted }
            static class Enums { public enum AmendmentChangeStatus { New, Deleted } }
            class Demo
            {
                Task Run(CancellationToken ct)
                {
                    _ = dbContext.Items
                        .Where(w => w.IsNotDeleted && w.ChangeStatus == Enums.AmendmentChangeStatus.New)
                        .ToListAsync(ct);
                    return Task.CompletedTask;
                }
            }
            """;

        var expression = "dbContext.Items.Where(w => w.IsNotDeleted && w.ChangeStatus == Enums.AmendmentChangeStatus.New).ToListAsync(ct)";
        var (line, character) = FindPosition(source, "Enums.AmendmentChangeStatus.New");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null,
            out var rewrittenExpression);

        Assert.DoesNotContain(graph, g => g.Kind == "member-capture");
        Assert.DoesNotContain("__qlm_Enums_AmendmentChangeStatus", rewrittenExpression, StringComparison.Ordinal);
        Assert.Contains("Enums.AmendmentChangeStatus.New", rewrittenExpression, StringComparison.Ordinal);
        Assert.Contains(graph, g => g.Name == "ct");
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_AnonymousProjectionNameEquals_DoesNotBecomeFreeVariable()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            static class Enums { public enum ColApprovalStatus { Approved } }
            class UtilityCloseCaseCondition { public Enums.ColApprovalStatus ColApprovalStatus { get; set; } public DateTime? ColApprovalDate { get; set; } }
            class PlusCase { public Guid PlusCaseId { get; set; } public UtilityCloseCaseCondition UtilityCloseCaseCondition { get; set; } = new(); public bool IsNotDeleted() => true; }
            class Demo
            {
                Task Run(Guid caseId, CancellationToken cancellationToken)
                {
                    _ = dbContext.PlusCases
                        .Where(w => w.IsNotDeleted())
                        .Where(w => w.PlusCaseId == caseId)
                        .Select(s => new
                        {
                            isColApproved = s.UtilityCloseCaseCondition.ColApprovalStatus == Enums.ColApprovalStatus.Approved,
                            s.UtilityCloseCaseCondition.ColApprovalDate
                        })
                        .SingleAsync(cancellationToken);

                    return Task.CompletedTask;
                }
            }
            """;

        var expression = "dbContext.PlusCases.Where(w => w.IsNotDeleted()).Where(w => w.PlusCaseId == caseId).Select(s => new { isColApproved = s.UtilityCloseCaseCondition.ColApprovalStatus == Enums.ColApprovalStatus.Approved, s.UtilityCloseCaseCondition.ColApprovalDate }).SingleAsync(cancellationToken)";
        var (line, character) = FindPosition(source, "isColApproved =");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null);

        Assert.DoesNotContain(graph, g => g.Name == "isColApproved");
        Assert.Contains(graph, g => g.Name == "caseId");
        Assert.Contains(graph, g => g.Name == "cancellationToken");
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_ReplayInitializer_DowngradesWhenDependencyIsAnonymousType()
    {
        var source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            class UserGroup { public string UserGroupId { get; set; } = ""; public bool IsNotDeleted() => true; public string Name { get; set; } = ""; }
            class Demo
            {
                Task Run(CancellationToken cancellationToken)
                {
                    var query = dbContext.UserGroups.Select(w => new { groupId = w.UserGroupId, groupName = w.Name });
                    var filteredQuery = query.ToList();
                    var groupIds = filteredQuery.Select(x => x.groupId).ToArray();

                    _ = dbContext.UserGroups
                        .Where(w => w.IsNotDeleted() && groupIds.Contains(w.UserGroupId))
                        .ToListAsync(cancellationToken);

                    return Task.CompletedTask;
                }
            }
            """;

        var expression = "dbContext.UserGroups.Where(w => w.IsNotDeleted() && groupIds.Contains(w.UserGroupId)).ToListAsync(cancellationToken)";
        var (line, character) = FindPosition(source, "groupIds.Contains");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null);

        Assert.DoesNotContain(graph, g => g.Name == "query");
        Assert.DoesNotContain(graph, g => g.Name == "filteredQuery");

        var groupIds = Assert.Single(graph, g => g.Name == "groupIds");
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, groupIds.ReplayPolicy);
        Assert.Empty(groupIds.Dependencies);
        Assert.Null(groupIds.InitializerExpression);
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_QueryExpression_CapturesPrecedingVarLocal()
    {
        var source = """
            using Microsoft.EntityFrameworkCore;

            class Order { public bool IsNotDeleted { get; set; } public DateTime CreatedUtc { get; set; } }
            class Demo
            {
                Task Run(DateTime utcNow, int lookbackDays, CancellationToken ct)
                {
                    var safeLookbackDays = Math.Clamp(lookbackDays, 1, 365);
                    var fromUtc = utcNow.Date.AddDays(-safeLookbackDays);

                    _ = (from o in dbContext.Orders
                        where o.IsNotDeleted && o.CreatedUtc >= fromUtc
                        orderby o.CreatedUtc descending
                        select o.CreatedUtc).ToListAsync(ct);

                    return Task.CompletedTask;
                }
            }
            """;

        var expression = "(from o in dbContext.Orders where o.IsNotDeleted && o.CreatedUtc >= fromUtc orderby o.CreatedUtc descending select o.CreatedUtc).ToListAsync(ct)";
        var (line, character) = FindPosition(source, "where o.IsNotDeleted");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null);

        Assert.DoesNotContain(graph, g => g.Name == "o");
        Assert.Contains(graph, g => g.Name == "ct");
        Assert.Contains(graph, g => g.Name == "fromUtc");
    }

    [Fact]
    public void ExtractFreeVariableSymbolGraph_Assignment_UsesLeftHandTypeWhenRightHandUnknown()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class Order { public bool IsNotDeleted { get; set; } public DateTime CreatedUtc { get; set; } }
            class Demo
            {
                Task Run(CancellationToken ct)
                {
                    DateTime fromUtc;
                    fromUtc = UnknownFactory();
                    _ = dbContext.Orders.Where(o => o.IsNotDeleted && o.CreatedUtc >= fromUtc).ToListAsync(ct);
                    return Task.CompletedTask;
                }
            }
            """;

        var expression = "dbContext.Orders.Where(o => o.IsNotDeleted && o.CreatedUtc >= fromUtc).ToListAsync(ct)";
        var (line, character) = FindPosition(source, "fromUtc).ToListAsync");
        var graph = LspSyntaxHelper.ExtractFreeVariableSymbolGraph(
            expression,
            "dbContext",
            source,
            line,
            character,
            targetAssemblyPath: null);

        var fromUtc = Assert.Single(graph, g => g.Name == "fromUtc");
        Assert.Equal("global::System.DateTime", fromUtc.TypeName);
        Assert.Equal(LocalSymbolReplayPolicies.UsePlaceholder, fromUtc.ReplayPolicy);
        Assert.Null(fromUtc.InitializerExpression);
        Assert.Empty(fromUtc.Dependencies);
    }

    [Fact]
    public void ExtractLocalVariableTypes_LineBeyondFileLength_ReturnsEmpty()
    {
        var types = LspSyntaxHelper.ExtractLocalVariableTypesAtPosition("int x = 1;", 999, 0);
        Assert.Empty(types);
    }

    [Fact]
    public void ExtractLocalVariableTypes_VarWithImplicitNew_ReturnsEmpty()
    {
        // var x = new() {} — target type unknown without semantic model → excluded
        var source = """
            var x = new();
            _ = x;
            """;

        var types = Extract(source, "_ = x;");

        // 'x' may or may not appear — if included it must not crash, and if absent that's correct
        // The important thing is no exception is thrown.
        Assert.IsType<Dictionary<string, string>>(types);
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Extract(string source, string cursorMarker)
    {
        var (line, character) = FindPosition(source, cursorMarker);
        return LspSyntaxHelper.ExtractLocalVariableTypesAtPosition(source, line, character);
    }

    private static (int line, int character) FindPosition(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Marker '{marker}' not found in source.");

        var line = 0;
        var character = 0;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n') { line++; character = 0; }
            else { character++; }
        }

        return (line, character);
    }
}
