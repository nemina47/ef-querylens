using System.Reflection;
using System.IO;
using System.Text.RegularExpressions;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using QueryEvaluator = EFQueryLens.Core.Scripting.Evaluation.QueryEvaluator;

namespace EFQueryLens.Core.Tests.Scripting;

public sealed class QueryEvaluatorFixture : IAsyncLifetime
{
    public ProjectAssemblyContext AlcCtx { get; private set; } = null!;
    public QueryEvaluator Evaluator { get; } = new();

    public Task InitializeAsync()
    {
        AlcCtx = new ProjectAssemblyContext(QueryEvaluatorTests.GetSampleMySqlAppDll());
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        AlcCtx.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Integration-style unit tests for <see cref="QueryEvaluator"/>.
///
/// Sample fixtures are copied into isolated subfolders under the test output dir
/// so transitive package DLLs do not overwrite each other.
/// </summary>
[Collection("QueryEvaluatorIsolation")]
public partial class QueryEvaluatorTests : IClassFixture<QueryEvaluatorFixture>
{
    private const string DefaultMySqlDbContextType = "SampleMySqlApp.Infrastructure.Persistence.MySqlAppDbContext";

    private readonly ProjectAssemblyContext _alcCtx;
    private readonly QueryEvaluator _evaluator;

    public QueryEvaluatorTests(QueryEvaluatorFixture fixture)
    {
        _alcCtx    = fixture.AlcCtx;
        _evaluator = fixture.Evaluator;
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    internal static string GetSampleMySqlAppDll()
    {
        var dir = Path.GetDirectoryName(typeof(QueryEvaluatorTests).Assembly.Location)!;
        var dll = ResolveSampleDll(dir, "SampleMySqlApp", "SampleMySqlApp.dll");

        if (!File.Exists(dll))
            throw new FileNotFoundException(
                $"SampleMySqlApp.dll not found in test output dir. Expected: {dll}");

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
        bool useAsyncRunner = false,
        CancellationToken ct = default) =>
        _evaluator.EvaluateAsync(_alcCtx,
            new TranslationRequest
            {
                AssemblyPath      = _alcCtx.AssemblyPath,
                Expression        = expression,
                DbContextTypeName = dbContextTypeName ?? DefaultMySqlDbContextType,
                AdditionalImports = additionalImports ?? [],
                UsingAliases = usingAliases
                    ?? new Dictionary<string, string>(StringComparer.Ordinal),
                UsingStaticTypes = usingStaticTypes ?? [],
                UseAsyncRunner = useAsyncRunner,
            }, ct);

    // ─── Basic translation ────────────────────────────────────────────────────

    // ─── Expression<Func<...>> as LocalVariableType ───────────────────────────


    // ─── Result shape ─────────────────────────────────────────────────────────

    private string BuildStubDeclarationForTest(string missingName, string expression)
        => BuildStubDeclarationForRequestForTest(missingName, expression);

    private string BuildStubDeclarationForRequestForTest(
        string missingName,
        string expression,
        IReadOnlyDictionary<string, string>? localVariableTypes = null,
        IReadOnlyDictionary<string, string>? usingAliases = null)
    {
        var dbContextType = _alcCtx.FindDbContextType(null, expression);
        var request = new TranslationRequest
        {
            AssemblyPath = _alcCtx.AssemblyPath,
            Expression = expression,
            LocalVariableTypes = localVariableTypes ?? new Dictionary<string, string>(StringComparer.Ordinal),
            UsingAliases = usingAliases ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

        var method = typeof(QueryEvaluator).GetMethod(
            "BuildStubDeclaration",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var stub = method!.Invoke(
            null,
            [missingName, "db", request, dbContextType]) as string;

        Assert.NotNull(stub);
        return stub!;
    }

    private static IReadOnlyList<Diagnostic> CreateCompilationErrors(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName: "GridifyPredicateTests",
            syntaxTrees: [tree],
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
    }

    private static bool InvokeHasMissingGridifyTypeErrors(IReadOnlyList<Diagnostic> errors)
    {
        var method = typeof(QueryEvaluator).GetMethod(
            "HasMissingGridifyTypeErrors",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var value = method!.Invoke(null, [errors]);
        return Assert.IsType<bool>(value);
    }

    private static IReadOnlyList<string> InvokeInferMissingExtensionStaticImports(
        IReadOnlyList<Diagnostic> errors,
        CSharpCompilation compilation,
        IReadOnlyList<Assembly> assemblies)
    {
        var method = typeof(QueryEvaluator).GetMethod(
            "InferMissingExtensionStaticImports",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var value = method!.Invoke(null, [errors, compilation, assemblies]);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(value);
    }

    private static bool InvokeShouldDumpGeneratedSource(QueryEvaluator evaluator)
    {
        var method = typeof(QueryEvaluator).GetMethod(
            "ShouldDumpGeneratedSource",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        var value = method!.Invoke(evaluator, null);
        return Assert.IsType<bool>(value);
    }

    private static string InvokeDumpGeneratedSourceToTemp(QueryEvaluator evaluator, string source)
    {
        var method = typeof(QueryEvaluator).GetMethod(
            "DumpGeneratedSourceToTemp",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        var value = method!.Invoke(evaluator, [source]);
        return Assert.IsType<string>(value);
    }
}

public static class ReadOnlySpanCaseExtensions
{
    public static string ToLower(this ReadOnlySpan<char> value)
        => value.ToString().ToLowerInvariant();
}
