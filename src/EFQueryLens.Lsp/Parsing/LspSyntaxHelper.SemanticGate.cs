using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Lsp.Parsing;

public static partial class LspSyntaxHelper
{
    private static readonly string[] DefaultImplicitUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Threading",
        "System.Threading.Tasks",
    ];

    internal static bool TryCreateSemanticModel(
        string sourceText,
        string? targetAssemblyPath,
        out SyntaxTree tree,
        out SyntaxNode root,
        out SemanticModel model)
    {
        tree = CSharpSyntaxTree.ParseText(sourceText);
        root = tree.GetRoot();
        var references = BuildSemanticGateMetadataReferences(targetAssemblyPath);
        var compilation = CSharpCompilation.Create(
            assemblyName: "__ql_semantic_gate__",
            syntaxTrees: [tree],
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                usings: DefaultImplicitUsings));
        model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        return true;
    }

    internal static bool IsSemanticallyQueryableExpression(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is InvocationExpressionSyntax invocation)
        {
            return IsSemanticallyQueryableInvocation(invocation, model);
        }

        if (expression is QueryExpressionSyntax queryExpression)
        {
            return IsQueryableLike(model.GetTypeInfo(queryExpression).ConvertedType);
        }

        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            return IsQueryableLike(model.GetTypeInfo(memberAccess.Expression).Type);
        }

        var converted = model.GetTypeInfo(expression).ConvertedType;
        return IsQueryableLike(converted);
    }

    internal static bool IsSemanticallyQueryableInvocation(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        return IsQueryableInvocation(invocation, model);
    }

    internal static bool PassesSemanticLinqGate(
        string sourceText,
        int line,
        int character,
        string? targetAssemblyPath,
        out string? reason)
    {
        reason = null;

        try
        {
            TryCreateSemanticModel(sourceText, targetAssemblyPath, out var tree, out var root, out var model);
            var textLines = tree.GetText().Lines;
            if (line < 0 || line >= textLines.Count)
            {
                reason = "cursor-out-of-range";
                return false;
            }

            var charOffset = Math.Min(Math.Max(character, 0), textLines[line].End - textLines[line].Start);
            var position = textLines[line].Start + charOffset;
            var node = root.FindToken(position).Parent;
            if (node is null)
            {
                reason = "cursor-node-missing";
                return false;
            }

            var queryExpression = node.FirstAncestorOrSelf<QueryExpressionSyntax>();
            if (queryExpression is not null && IsQueryableLike(model.GetTypeInfo(queryExpression).ConvertedType))
            {
                return true;
            }

            var outermostInvocations = node.AncestorsAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Select(GetOutermostInvocationChain)
                .GroupBy(i => (i.SpanStart, i.Span.Length))
                .Select(g => g.First())
                .ToArray();

            foreach (var invocation in outermostInvocations)
            {
                if (IsQueryableInvocation(invocation, model))
                {
                    return true;
                }
            }

            var memberAccess = node.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
            if (memberAccess is not null)
            {
                var receiverType = model.GetTypeInfo(memberAccess.Expression).Type;
                if (IsQueryableLike(receiverType))
                {
                    return true;
                }
            }

            reason = "not-semantic-queryable";
            return false;
        }
        catch
        {
            reason = "semantic-gate-error";
            return false;
        }
    }

    internal static bool PassesExtractedExpressionLinqShapeGate(
        string expression,
        string contextVariableName)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(contextVariableName))
            return false;

        try
        {
            var parsed = SyntaxFactory.ParseExpression(expression);
            if (parsed.ContainsDiagnostics)
                return false;

            var rootContext = TryExtractRootContextVariable(parsed);
            if (!string.Equals(rootContext, contextVariableName, StringComparison.Ordinal))
                return false;

            if (parsed is InvocationExpressionSyntax invocation)
            {
                var methodNames = GetInvocationChainMethodNames(invocation).ToArray();
                if (methodNames.Any(name => QueryChainMethods.Contains(name) || TerminalMethods.Contains(name)))
                    return true;
            }

            var anyInvocation = parsed.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .SelectMany(GetInvocationChainMethodNames)
                .Any(name => QueryChainMethods.Contains(name) || TerminalMethods.Contains(name));

            return anyInvocation;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsQueryableInvocation(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        foreach (var call in invocation.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var method = ResolveMethodSymbol(model, call);
            if (method is null)
            {
                continue;
            }

            var container = method.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty, StringComparison.Ordinal);
            if (string.Equals(container, "System.Linq.Enumerable", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(container, "System.Linq.Queryable", StringComparison.Ordinal)
                || string.Equals(container, "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", StringComparison.Ordinal))
            {
                return true;
            }

            if (IsQueryableLike(method.ReturnType))
            {
                return true;
            }

            if (call.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var receiverType = model.GetTypeInfo(memberAccess.Expression).Type;
                if (IsQueryableLike(receiverType))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IMethodSymbol? ResolveMethodSymbol(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        var info = model.GetSymbolInfo(invocation);
        if (info.Symbol is IMethodSymbol method)
        {
            return method;
        }

        return info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    private static bool IsQueryableLike(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        var allTypes = EnumerateSelfAndBaseTypes(type)
            .Concat(type.AllInterfaces)
            .OfType<INamedTypeSymbol>();

        foreach (var candidate in allTypes)
        {
            var display = candidate.OriginalDefinition
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty, StringComparison.Ordinal);

            if (string.Equals(display, "System.Linq.IQueryable<T>", StringComparison.Ordinal)
                || string.Equals(display, "System.Linq.IQueryable", StringComparison.Ordinal)
                || string.Equals(display, "System.Linq.IOrderedQueryable<T>", StringComparison.Ordinal)
                || string.Equals(display, "Microsoft.EntityFrameworkCore.DbSet<T>", StringComparison.Ordinal)
                || string.Equals(display, "System.Collections.Generic.IAsyncEnumerable<T>", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<ITypeSymbol> EnumerateSelfAndBaseTypes(ITypeSymbol type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static IReadOnlyList<MetadataReference> BuildSemanticGateMetadataReferences(string? targetAssemblyPath)
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddReference(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            var full = Path.GetFullPath(path);
            if (!seen.Add(full))
            {
                return;
            }

            references.Add(MetadataReference.CreateFromFile(full));
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa
            && !string.IsNullOrWhiteSpace(tpa))
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                AddReference(path);
            }
        }

        if (!string.IsNullOrWhiteSpace(targetAssemblyPath))
        {
            AddReference(targetAssemblyPath);

            var assemblyDir = Path.GetDirectoryName(targetAssemblyPath);
            if (!string.IsNullOrWhiteSpace(assemblyDir) && Directory.Exists(assemblyDir))
            {
                foreach (var dll in Directory.EnumerateFiles(assemblyDir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    AddReference(dll);
                }
            }
        }

        return references;
    }
}
