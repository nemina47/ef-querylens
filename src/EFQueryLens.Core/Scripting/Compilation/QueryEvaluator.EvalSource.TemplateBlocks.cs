using System.Text;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    private static void AppendBaseUsings(StringBuilder sb)
    {
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Collections;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Data;");
        sb.AppendLine("using System.Data.Common;");
        sb.AppendLine("using System.Globalization;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.EntityFrameworkCore;");
    }

    private static void AppendRequestUsings(
        StringBuilder sb,
        TranslationRequest request,
        IReadOnlySet<string> knownNamespaces,
        IReadOnlySet<string> knownTypes,
        IReadOnlyCollection<string> synthesizedUsingStaticTypes,
        IReadOnlyCollection<string> synthesizedUsingNamespaces)
    {
        foreach (var import in request.AdditionalImports)
        {
            if (IsValidUsingName(import) && IsResolvableNamespace(import, knownNamespaces))
                sb.AppendLine($"using {import};");
        }

        foreach (var ns in synthesizedUsingNamespaces
                     .Where(n => IsValidUsingName(n) && IsResolvableNamespace(n, knownNamespaces))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            sb.AppendLine($"using {ns};");
        }

        foreach (var kvp in request.UsingAliases
                     .Where(kvp => IsValidAliasName(kvp.Key)
                                   && IsValidUsingName(kvp.Value)
                                   && IsResolvableTypeOrNamespace(kvp.Value, knownNamespaces, knownTypes))
                     .OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"using {kvp.Key} = {kvp.Value};");
        }

        foreach (var st in request.UsingStaticTypes
                     .Where(st => IsValidUsingName(st) && IsResolvableType(st, knownTypes))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            sb.AppendLine($"using static {st};");
        }

        foreach (var st in synthesizedUsingStaticTypes
                     .Where(IsValidUsingName)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            sb.AppendLine($"using static {st};");
        }
    }

    private static void AppendRunner(
        StringBuilder sb,
        Type dbContextType,
        TranslationRequest request,
        IReadOnlyList<string> stubs)
    {
        sb.AppendLine();

        var contextDeclaration =
            $"        var {request.ContextVariableName} = ({dbContextType.FullName!.Replace('+', '.')})(object)__ctx__;";

        var stubsBlock = stubs.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, stubs.Select(static stub => $"        {stub}"));

        var renderedRunner = EvalSourceTemplateCatalog.Render(
            EvalSourceTemplateCatalog.Runner,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["__QL_CONTEXT_DECL__"] = contextDeclaration,
                ["__QL_STUBS__"] = stubsBlock,
                ["__QL_CONTEXT_VAR__"] = request.ContextVariableName,
                ["__QL_EXPRESSION__"] = request.Expression,
            });

        sb.Append(renderedRunner);
    }

    private static void AppendFallbackExtensions(StringBuilder sb, bool includeGridifyFallbackExtensions)
    {
        if (!includeGridifyFallbackExtensions)
            return;

        sb.AppendLine();
        sb.AppendLine("internal static class __QueryLensGridifyFallbackExtensions__");
        sb.AppendLine("{");
        sb.AppendLine("    public static System.Linq.IQueryable<T> ApplyFilteringAndOrdering<T>(");
        sb.AppendLine("        this System.Linq.IQueryable<T> source,");
        sb.AppendLine("        object? query) => source;");
        sb.AppendLine();
        sb.AppendLine("    public static System.Linq.IQueryable<T> ApplyFilteringAndOrdering<T>(");
        sb.AppendLine("        this System.Linq.IQueryable<T> source,");
        sb.AppendLine("        object? query,");
        sb.AppendLine("        object? mapper) => source;");
        sb.AppendLine();
        sb.AppendLine("    public static System.Linq.IQueryable<T> ApplyPaging<T>(");
        sb.AppendLine("        this System.Linq.IQueryable<T> source,");
        sb.AppendLine("        int page,");
        sb.AppendLine("        int pageSize) => source;");
        sb.AppendLine("}");
    }
}
