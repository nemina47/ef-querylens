using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using EFQueryLens.Core.Scripting.Compilation;

namespace EFQueryLens.Core.Scripting;

public sealed partial class QueryEvaluator
{
    private static string BuildEvalSource(
        Type dbContextType,
        TranslationRequest request,
        IReadOnlyList<string> stubs,
        IReadOnlySet<string> knownNamespaces,
        IReadOnlySet<string> knownTypes,
        IReadOnlyCollection<string> synthesizedUsingStaticTypes)
    {
        var sb = new StringBuilder();

        AppendBaseUsings(sb);
        AppendRequestUsings(sb, request, knownNamespaces, knownTypes, synthesizedUsingStaticTypes);
        sb.AppendLine();
        sb.Append(EvalSourceTemplateCatalog.CapturedTypes);
        sb.AppendLine();
        sb.Append(EvalSourceTemplateCatalog.OfflineDbConnection);
        sb.AppendLine();
        sb.Append(EvalSourceTemplateCatalog.FakeDbDataReader);
        sb.AppendLine();
        sb.Append(EvalSourceTemplateCatalog.SqlCaptureScope);
        sb.AppendLine();
        sb.Append(EvalSourceTemplateCatalog.OfflineCapture);
        AppendRunner(sb, dbContextType, request, stubs);

        return sb.ToString();
    }

    private static CSharpCompilation BuildCompilation(string source, MetadataReference[] refs)
    {
        var tree = CSharpSyntaxTree.ParseText(source, SParseOptions);
        return CSharpCompilation.Create(
            $"__QueryLensEval_{Guid.NewGuid():N}",
            syntaxTrees: [tree],
            references: refs,
            options: SCompilationOptions);
    }
}
