using System.Text;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    private static string BuildEvalSource(
        Type dbContextType,
        TranslationRequest request,
        IReadOnlyList<string> stubs,
        IReadOnlySet<string> knownNamespaces,
        IReadOnlySet<string> knownTypes,
        IReadOnlyCollection<string> synthesizedUsingStaticTypes,
        IReadOnlyCollection<string> synthesizedUsingNamespaces,
        bool includeGridifyFallbackExtensions)
    {
        var sb = new StringBuilder();
        var emittedUsings = new HashSet<string>(StringComparer.Ordinal);

        sb.AppendLine("#region Metadata");
        AppendBaseUsings(sb, emittedUsings);
        AppendRequestUsings(sb, emittedUsings, request, knownNamespaces, knownTypes, synthesizedUsingStaticTypes, synthesizedUsingNamespaces);
        sb.AppendLine("#endregion");
        sb.AppendLine();
        
        sb.AppendLine("#region Type Definitions");
        sb.Append(EvalSourceTemplateCatalog.CapturedTypes);
        sb.AppendLine("#endregion");
        sb.AppendLine();
        
        sb.AppendLine("#region Offline Infrastructure");
        sb.Append(EvalSourceTemplateCatalog.OfflineDbConnection);
        sb.AppendLine();
        sb.Append(EvalSourceTemplateCatalog.FakeDbDataReader);
        sb.AppendLine("#endregion");
        sb.AppendLine();
        
        sb.AppendLine("#region SQL Capture");
        sb.Append(EvalSourceTemplateCatalog.SqlCaptureScope);
        sb.AppendLine("#endregion");
        sb.AppendLine();
        
        sb.AppendLine("#region Capture Setup");
        sb.Append(EvalSourceTemplateCatalog.OfflineCapture);
        sb.AppendLine("#endregion");
        sb.AppendLine();
        
        AppendFallbackExtensions(sb, includeGridifyFallbackExtensions);
        
        sb.AppendLine("#region Execution");
        AppendRunner(sb, dbContextType, request, stubs);
        sb.AppendLine("#endregion");

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
