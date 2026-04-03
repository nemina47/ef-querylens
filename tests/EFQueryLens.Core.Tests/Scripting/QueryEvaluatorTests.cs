using System.Reflection;
using System.IO;
using System.Text.RegularExpressions;
using EFQueryLens.Core.AssemblyContext;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using EvalSourceBuilder = EFQueryLens.Core.Scripting.Evaluation.EvalSourceBuilder;
using ImportResolver = EFQueryLens.Core.Scripting.Evaluation.ImportResolver;
using QueryEvaluator = EFQueryLens.Core.Scripting.Evaluation.QueryEvaluator;
using StubSynthesizer = EFQueryLens.Core.Scripting.Evaluation.StubSynthesizer;

namespace EFQueryLens.Core.Tests.Scripting;

public sealed class QueryEvaluatorFixture : IAsyncLifetime
{
    public ProjectAssemblyContext AlcCtx { get; private set; } = null!;
    public QueryEvaluator Evaluator { get; } = new();

    public ValueTask InitializeAsync()
    {
        AlcCtx = new ProjectAssemblyContext(QueryEvaluatorTests.GetSampleMySqlAppDll());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        AlcCtx.Dispose();
        return ValueTask.CompletedTask;
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
        IReadOnlyDictionary<string, string>? localVariableTypes = null,
        IReadOnlyList<LocalSymbolHint>? localSymbolHints = null,
        IReadOnlyList<MemberTypeHint>? memberTypeHints = null,
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
                LocalSymbolGraph = BuildSymbolGraph(
                    localVariableTypes ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    localSymbolHints ?? []),
                UseAsyncRunner = useAsyncRunner,
            }, ct);

    private Task<QueryTranslationResult> TranslateStrictAsync(
        string expression,
        IReadOnlyDictionary<string, string>? localVariableTypes = null,
        IReadOnlyList<LocalSymbolHint>? localSymbolHints = null,
        IReadOnlyList<MemberTypeHint>? memberTypeHints = null,
        string? dbContextTypeName = null,
        IReadOnlyList<string>? additionalImports = null,
        IReadOnlyDictionary<string, string>? usingAliases = null,
        IReadOnlyList<string>? usingStaticTypes = null,
        bool useAsyncRunner = false,
        CancellationToken ct = default) =>
        TranslateAsync(
            expression,
            dbContextTypeName,
            additionalImports,
            usingAliases,
            usingStaticTypes,
            localVariableTypes,
            localSymbolHints,
            memberTypeHints,
            useAsyncRunner,
            ct);

    // ─── Basic translation ────────────────────────────────────────────────────

    // ─── Expression<Func<...>> as LocalVariableType ───────────────────────────


    // ─── Result shape ─────────────────────────────────────────────────────────

    private string BuildStubDeclarationForTest(
        string missingName,
        string expression)
        => BuildStubDeclarationForRequestForTest(
            missingName,
            expression);

    private string BuildStubDeclarationForRequestForTest(
        string missingName,
        string expression,
        IReadOnlyDictionary<string, string>? localVariableTypes = null,
        IReadOnlyDictionary<string, string>? usingAliases = null,
        IReadOnlyList<LocalSymbolHint>? localSymbolHints = null,
        IReadOnlyList<MemberTypeHint>? memberTypeHints = null)
    {
        var dbContextType = _alcCtx.FindDbContextType(null, expression);
        var request = new TranslationRequest
        {
            AssemblyPath = _alcCtx.AssemblyPath,
            Expression = expression,
            LocalSymbolGraph = BuildSymbolGraph(
                localVariableTypes ?? new Dictionary<string, string>(StringComparer.Ordinal),
                localSymbolHints ?? []),
            UsingAliases = usingAliases ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

        var method = typeof(StubSynthesizer).GetMethod(
            "BuildStubDeclaration",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);

        var stub = method!.Invoke(
            null,
            [missingName, "db", request, dbContextType]) as string;

        Assert.NotNull(stub);
        return stub!;
    }

    private static IReadOnlyList<LocalSymbolGraphEntry> BuildSymbolGraph(
        IReadOnlyDictionary<string, string> localVariableTypes,
        IReadOnlyList<LocalSymbolHint> localSymbolHints)
    {
        var byName = new Dictionary<string, LocalSymbolGraphEntry>(StringComparer.Ordinal);
        var order = 0;

        foreach (var hint in localSymbolHints)
        {
            if (string.IsNullOrWhiteSpace(hint.Name) || string.IsNullOrWhiteSpace(hint.TypeName))
            {
                continue;
            }

            byName[hint.Name] = new LocalSymbolGraphEntry
            {
                Name = hint.Name,
                TypeName = hint.TypeName,
                Kind = hint.Kind,
                InitializerExpression = hint.InitializerExpression,
                DeclarationOrder = hint.DeclarationOrder > 0 ? hint.DeclarationOrder : order++,
                Dependencies = hint.Dependencies,
                Scope = hint.Scope,
                ReplayPolicy = hint.ReplayPolicy,
            };
        }

        foreach (var kv in localVariableTypes.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
            {
                continue;
            }

            if (byName.ContainsKey(kv.Key))
            {
                continue;
            }

            byName[kv.Key] = new LocalSymbolGraphEntry
            {
                Name = kv.Key,
                TypeName = kv.Value,
                Kind = "local",
                DeclarationOrder = order++,
                ReplayPolicy = LocalSymbolReplayPolicies.UsePlaceholder,
            };
        }

        return byName.Values
            .OrderBy(s => s.DeclarationOrder)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> InvokeInferMissingExtensionStaticImports(
        IReadOnlyList<Diagnostic> errors,
        CSharpCompilation compilation,
        IReadOnlyList<Assembly> assemblies)
    {
        var method = typeof(ImportResolver).GetMethod(
            "InferMissingExtensionStaticImports",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);

        var value = method!.Invoke(null, [errors, compilation, assemblies]);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(value);
    }

    private static bool InvokeShouldDumpGeneratedSource(QueryEvaluator evaluator)
    {
        var field = typeof(QueryEvaluator).GetField(
            "_dumpSourceEnabled",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        var value = field!.GetValue(evaluator);
        return Assert.IsType<bool>(value);
    }

    private static string InvokeDumpGeneratedSourceToTemp(string source)
    {
        var method = typeof(QueryEvaluator).GetMethod(
            "DumpGeneratedSourceToTemp",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var value = method!.Invoke(null, [source]);
        return Assert.IsType<string>(value);
    }
}

public static class ReadOnlySpanCaseExtensions
{
    public static string ToLower(this ReadOnlySpan<char> value)
        => value.ToString().ToLowerInvariant();
}
