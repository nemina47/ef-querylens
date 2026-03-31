using System.Reflection;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    [Fact]
    public async Task Evaluate_DtoInSameNamespaceAsCallingClass_AutoResolvesUsing()
    {
        const string expression =
            "db.Orders" +
            "    .Where(o => o.CreatedAt >= DateTime.UtcNow.AddDays(-30))" +
            "    .GroupBy(o => new { o.Customer.CustomerId, o.Customer.Name })" +
            "    .Select(g => new CustomerRevenueDto(" +
            "        g.Key.CustomerId," +
            "        g.Key.Name," +
            "        g.Count()," +
            "        g.Sum(o => o.Total)," +
            "        g.Average(o => o.Total)))";

        var result = await TranslateAsync(expression);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("CustomerRevenueDto", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_RootWrapperContextHop_IsNormalizedFromCompilerDiagnostics()
    {
        var result = await TranslateAsync("services.Context.Orders.Select(o => o.Id)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.DoesNotContain("does not contain a definition for 'Context'", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
        Assert.DoesNotContain("underlying reader doesn't have as many fields", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_ExpressionSelectorNestedToList_NoPartialRiskWarning()
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
        Assert.DoesNotContain(result.Warnings, w => string.Equals(w.Code, "QL_EXPRESSION_PARTIAL_RISK", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_AsyncRunnerMode_SimpleDbSet_ReturnsSql()
    {
        var result = await TranslateAsync("db.Orders", useAsyncRunner: true);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("Orders", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_AsyncRunnerMode_AsyncTerminal_ReturnsSql()
    {
        var result = await TranslateAsync("db.Orders.Where(o => o.UserId == 5).ToListAsync(ct)", useAsyncRunner: true);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
        Assert.Contains("WHERE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_FindCall_RewritesPkTypeAndReturnsSql()
    {
        var result = await TranslateAsync("db.Orders.Find(someId)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
    }

    [Fact]
    public async Task Evaluate_FindAsyncCall_RewritesPkTypeAndReturnsSql()
    {
        var result = await TranslateAsync("db.Orders.FindAsync(someId)");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Sql);
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
    public void InferMissingExtensionStaticImports_CS7036_UsesInvocationArityAndFindsZeroArgExtension()
    {
        const string source = """
using System;
public sealed class C
{
    public string M(ReadOnlySpan<char> span)
    {
        return span.ToLower();
    }
}
""";

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => a.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: "ExtensionImportInferenceCs7036",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.Contains(errors, e => e.Id == "CS7036");

        var imports = InvokeInferMissingExtensionStaticImports(errors, compilation, [typeof(QueryEvaluatorTests).Assembly]);

        Assert.Contains(typeof(ReadOnlySpanCaseExtensions).FullName!, imports, StringComparer.Ordinal);
        Assert.DoesNotContain("System.MemoryExtensions", imports, StringComparer.Ordinal);
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
}
