using System.Text;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    private static void AppendBaseUsings(StringBuilder sb, HashSet<string> emittedUsings)
    {
        AppendUsingLine(sb, emittedUsings, "using System;");
        AppendUsingLine(sb, emittedUsings, "using System.Linq;");
        AppendUsingLine(sb, emittedUsings, "using System.Collections;");
        AppendUsingLine(sb, emittedUsings, "using System.Collections.Generic;");
        AppendUsingLine(sb, emittedUsings, "using System.Data;");
        AppendUsingLine(sb, emittedUsings, "using System.Data.Common;");
        AppendUsingLine(sb, emittedUsings, "using System.Globalization;");
        AppendUsingLine(sb, emittedUsings, "using System.Reflection;");
        AppendUsingLine(sb, emittedUsings, "using System.Threading;");
        AppendUsingLine(sb, emittedUsings, "using System.Threading.Tasks;");
        AppendUsingLine(sb, emittedUsings, "using Microsoft.EntityFrameworkCore;");
        AppendUsingLine(sb, emittedUsings, "using EFQueryLens.Core.Scripting.Contracts;");
    }

    private static void AppendRequestUsings(
        StringBuilder sb,
        HashSet<string> emittedUsings,
        TranslationRequest request,
        IReadOnlySet<string> knownNamespaces,
        IReadOnlySet<string> knownTypes,
        IReadOnlyCollection<string> synthesizedUsingStaticTypes,
        IReadOnlyCollection<string> synthesizedUsingNamespaces)
    {
        foreach (var import in request.AdditionalImports
                     .Where(i => IsValidUsingName(i) && IsResolvableNamespace(i, knownNamespaces))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            AppendUsingLine(sb, emittedUsings, $"using {import};");
        }

        foreach (var ns in synthesizedUsingNamespaces
                     .Where(n => IsValidUsingName(n) && IsResolvableNamespace(n, knownNamespaces))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            AppendUsingLine(sb, emittedUsings, $"using {ns};");
        }

        foreach (var kvp in request.UsingAliases
                     .Where(kvp => IsValidAliasName(kvp.Key)
                                   && IsValidUsingName(kvp.Value)
                                   && IsResolvableTypeOrNamespace(kvp.Value, knownNamespaces, knownTypes))
                     .OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            AppendUsingLine(sb, emittedUsings, $"using {kvp.Key} = {kvp.Value};");
        }

        foreach (var st in request.UsingStaticTypes
                     .Where(st => IsValidUsingName(st) && IsResolvableType(st, knownTypes))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            AppendUsingLine(sb, emittedUsings, $"using static {st};");
        }

        foreach (var st in synthesizedUsingStaticTypes
                     .Where(IsValidUsingName)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            AppendUsingLine(sb, emittedUsings, $"using static {st};");
        }
    }

    private static void AppendUsingLine(StringBuilder sb, HashSet<string> emittedUsings, string line)
    {
        if (emittedUsings.Add(line))
        {
            sb.AppendLine(line);
        }
    }

    private static void AppendRunner(
        StringBuilder sb,
        Type dbContextType,
        TranslationRequest request,
        IReadOnlyList<string> stubs)
    {
        // Log: final stubs before compilation
        if (stubs.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[QL-Eval] eval-stubs count={stubs.Count} stubs={{{string.Join(";", stubs.Select(s => s.Trim()))}}}");
        }

        sb.AppendLine();
        sb.Append(
            RunnerGenerator.GenerateRunnerClass(
                request.ContextVariableName,
                dbContextType.FullName!.Replace('+', '.'),
                request.Expression,
                stubs,
                request.UseAsyncRunner));
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
