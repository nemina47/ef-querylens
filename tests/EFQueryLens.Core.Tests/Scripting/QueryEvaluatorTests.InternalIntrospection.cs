using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using QueryEvaluator = EFQueryLens.Core.Scripting.Evaluation.QueryEvaluator;

namespace EFQueryLens.Core.Tests.Scripting;

public partial class QueryEvaluatorTests
{
    [Fact]
    public void CustomerRevenueDto_IsInKnownTypesForLoadedAssemblies()
    {
        var allTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asm in _alcCtx.LoadedAssemblies)
        {
            try
            {
                foreach (var t in asm.GetTypes())
                    if (!string.IsNullOrWhiteSpace(t.FullName))
                        allTypes.Add(t.FullName.Replace('+', '.'));
            }
            catch (ReflectionTypeLoadException rtle)
            {
                foreach (var t in rtle.Types)
                    if (t?.FullName is { } fn)
                        allTypes.Add(fn.Replace('+', '.'));
            }
        }

        var fullName = allTypes.FirstOrDefault(n => n.EndsWith(".CustomerRevenueDto", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(fullName),
            "CustomerRevenueDto not found in loaded assemblies. " +
            $"Types searched: {allTypes.Count}. " +
            "The stale net10.0 DLL may predate the type's addition.");

        var parents = QueryEvaluator.FindNamespacesForSimpleName("CustomerRevenueDto", allTypes).ToList();
        Assert.NotEmpty(parents);
        Assert.Contains(parents, p => p.Contains("CustomerReadService", StringComparison.Ordinal));
    }

    [Fact]
    public void FindNamespacesForSimpleName_KnownType_ReturnsNamespace()
    {
        var knownTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "SampleMySqlApp.Application.Customers.CustomerRevenueDto",
            "SampleMySqlApp.Domain.Entities.Order",
            "System.String",
        };

        var result = QueryEvaluator.FindNamespacesForSimpleName("CustomerRevenueDto", knownTypes).ToList();

        Assert.Single(result);
        Assert.Equal("SampleMySqlApp.Application.Customers", result[0]);
    }

    [Fact]
    public void TryExtractExpectedTypeFromCS1503_StringNullable_ReturnsStringNullable()
    {
        const string src = """
            class C
            {
                void M(string? s) { }
                void Test()
                {
                    object x = null!;
                    M(x);
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create(
            "test",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var cs1503 = compilation.GetDiagnostics().FirstOrDefault(d => d.Id == "CS1503");

        Assert.NotNull(cs1503);
        var expected = QueryEvaluator.TryExtractExpectedTypeFromCS1503(cs1503!);
        Assert.Equal("string?", expected);
    }

    [Fact]
    public void EmbeddedTemplate_FakeDbDataReader_QualifiesSystemType()
    {
        var assembly = typeof(QueryEvaluator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name =>
                name.EndsWith(".Scripting.Compilation.Templates.FakeDbDataReader.cs.tmpl", StringComparison.Ordinal));

        Assert.False(string.IsNullOrWhiteSpace(resourceName));

        using var stream = assembly.GetManifestResourceStream(resourceName!);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var template = reader.ReadToEnd();

        Assert.Contains("public override global::System.Type GetFieldType", template, StringComparison.Ordinal);
        Assert.DoesNotContain("public override Type GetFieldType", template, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEvalSource_UsesQualifiedSystemTypeInGeneratedReader()
    {
        var dbContextType = _alcCtx.FindDbContextType("MySqlAppDbContext");
        var request = new TranslationRequest
        {
            AssemblyPath = _alcCtx.AssemblyPath,
            Expression = "db.Orders",
            ContextVariableName = "db",
            AdditionalImports = [],
            UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
            UsingStaticTypes = [],
        };

        var buildEvalSourceMethod = typeof(QueryEvaluator).GetMethod(
            "BuildEvalSource",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildEvalSourceMethod);

        var source = buildEvalSourceMethod!.Invoke(
            null,
            [
                dbContextType,
                request,
                Array.Empty<string>(),
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                Array.Empty<string>(),
                Array.Empty<string>(),
                false,
            ]) as string;

        Assert.False(string.IsNullOrWhiteSpace(source));
        Assert.Contains("public override global::System.Type GetFieldType", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public override Type GetFieldType", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEvalSource_DeduplicatesUsingDirectives()
    {
        var dbContextType = _alcCtx.FindDbContextType("MySqlAppDbContext");
        var request = new TranslationRequest
        {
            AssemblyPath = _alcCtx.AssemblyPath,
            Expression = "db.Orders",
            ContextVariableName = "db",
            AdditionalImports =
            [
                "System",
                "System.Linq",
                "System.Linq",
                "SampleMySqlApp.Domain.Entities",
            ],
            UsingAliases = new Dictionary<string, string>(StringComparer.Ordinal),
            UsingStaticTypes = ["System.Math", "System.Math"],
        };

        var buildEvalSourceMethod = typeof(QueryEvaluator).GetMethod(
            "BuildEvalSource",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildEvalSourceMethod);

        var source = buildEvalSourceMethod!.Invoke(
            null,
            [
                dbContextType,
                request,
                Array.Empty<string>(),
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "System",
                    "System.Linq",
                    "SampleMySqlApp.Domain.Entities",
                },
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "System.Math",
                    "SampleMySqlApp.Domain.Entities.Order",
                },
                Array.Empty<string>(),
                Array.Empty<string>(),
                false,
            ]) as string;

        Assert.False(string.IsNullOrWhiteSpace(source));

        var usingLines = source!
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("using ", StringComparison.Ordinal))
            .ToArray();

        var duplicates = usingLines
            .GroupBy(line => line, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void QueryEvaluator_DumpSourceFlag_RespectsEnvironmentVariable()
    {
        var previous = Environment.GetEnvironmentVariable("QUERYLENS_DUMP_SOURCE");
        try
        {
            Environment.SetEnvironmentVariable("QUERYLENS_DUMP_SOURCE", "true");
            var enabledEvaluator = new QueryEvaluator();
            var enabled = InvokeShouldDumpGeneratedSource(enabledEvaluator);
            Assert.True(enabled);

            Environment.SetEnvironmentVariable("QUERYLENS_DUMP_SOURCE", null);
            var disabledEvaluator = new QueryEvaluator();
            var disabled = InvokeShouldDumpGeneratedSource(disabledEvaluator);
            Assert.False(disabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUERYLENS_DUMP_SOURCE", previous);
        }
    }

    [Fact]
    public void DumpGeneratedSourceToTemp_WritesTimestampedFile()
    {
        var evaluator = new QueryEvaluator();
        var source = "public sealed class __QueryLensRunner__ { }";

        var path = InvokeDumpGeneratedSourceToTemp(evaluator, source);

        try
        {
            Assert.NotEqual("(could not write temp file)", path);
            Assert.True(File.Exists(path));
            Assert.Matches(new Regex(@"^ql_eval_\d{8}_\d{6}_\d{3}(?:_\d+)?\.cs$", RegexOptions.CultureInvariant), Path.GetFileName(path));
            var written = File.ReadAllText(path);
            Assert.Equal(source, written);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
