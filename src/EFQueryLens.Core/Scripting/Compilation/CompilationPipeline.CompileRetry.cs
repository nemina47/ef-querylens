using System.Reflection;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Core.Scripting.Evaluation;

internal sealed partial class CompilationPipeline
{
    private static string? TryExtractMissingIdentifierFromDiagnostic(Diagnostic diagnostic)
    {
        if (!diagnostic.Location.IsInSource || diagnostic.Location.SourceTree is null)
            return null;

        var root = diagnostic.Location.SourceTree.GetRoot();
        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

        var identifier = node as IdentifierNameSyntax
            ?? node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault()
            ?? node.AncestorsAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault();

        if (identifier is not null)
            return identifier.Identifier.ValueText;

        var token = root.FindToken(diagnostic.Location.SourceSpan.Start);
        return token.IsKind(SyntaxKind.IdentifierToken)
            ? token.ValueText
            : null;
    }

    private bool ApplyCompileRetryAdjustments(
        IReadOnlyList<Diagnostic> errors,
        CSharpCompilation compilation,
        IReadOnlyList<Assembly> compilationAssemblies,
        IReadOnlySet<string> knownNamespaces,
        IReadOnlySet<string> knownTypes,
        List<string> stubs,
        ISet<string> synthesizedUsingStaticTypes,
        ISet<string> synthesizedUsingNamespaces)
    {
        var changed = false;

        changed |= ApplyMissingTypeImportRule(errors, knownNamespaces, knownTypes, synthesizedUsingStaticTypes, synthesizedUsingNamespaces);
        changed |= ApplyMissingExtensionImportRule(errors, compilation, compilationAssemblies, synthesizedUsingStaticTypes);
        changed |= ReorderStubsByDependency(stubs);

        return changed;
    }

    private bool ApplyMissingTypeImportRule(
        IReadOnlyList<Diagnostic> errors,
        IReadOnlySet<string> knownNamespaces,
        IReadOnlySet<string> knownTypes,
        ISet<string> synthesizedUsingStaticTypes,
        ISet<string> synthesizedUsingNamespaces)
    {
        // CS0246 / CS0234: type or namespace not found. The type likely lives in a
        // namespace that the source file doesn't need to import explicitly (e.g. a DTO
        // in the same namespace as the calling class). Locate it in the loaded
        // assemblies and synthesise a using directive automatically.
        // Use the diagnostic message text (same regex as FormatSoftDiagnostics) to
        // extract the simple type name - more reliable than AST span lookup.
        //
        // Two cases:
        //  * Top-level type   -> parent is a namespace -> emit "using Ns;"
        //  * Nested type      -> parent is a class     -> emit "using static EnclosingType;"
        var changed = false;
        var missingTypes = errors
            .Where(d => d.Id is "CS0246" or "CS0234")
            .Select(QueryEvaluator.TryExtractTypeNameFromCS0246)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        LogDebug($"compile-retry cs0246-types={string.Join(",", missingTypes)}");

        foreach (var typeName in missingTypes)
        {
            var parents = ImportResolver.FindNamespacesForSimpleName(typeName!, knownTypes).ToList();
            LogDebug($"compile-retry type-lookup name={typeName} found-parents={string.Join(",", parents)}");

            if (parents.Count == 0)
            {
                LogDebug($"compile-retry type-not-in-known-types name={typeName}");
            }

            foreach (var parent in parents)
            {
                if (ImportResolver.IsResolvableNamespace(parent, knownNamespaces))
                {
                    if (synthesizedUsingNamespaces.Add(parent))
                    {
                        LogDebug($"compile-retry using-namespace added={parent}");
                        changed = true;
                    }
                }
                else if (ImportResolver.IsResolvableType(parent, knownTypes))
                {
                    if (synthesizedUsingStaticTypes.Add(parent))
                    {
                        LogDebug($"compile-retry using-static added={parent}");
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    private static bool ApplyMissingExtensionImportRule(
        IReadOnlyList<Diagnostic> errors,
        CSharpCompilation compilation,
        IReadOnlyList<Assembly> compilationAssemblies,
        ISet<string> synthesizedUsingStaticTypes)
    {
        var changed = false;
        foreach (var import in ImportResolver.InferMissingExtensionStaticImports(errors, compilation, compilationAssemblies))
        {
            if (synthesizedUsingStaticTypes.Add(import))
            {
                changed = true;
            }
        }

        return changed;
    }

    private static bool ReorderStubsByDependency(List<string> stubs)
    {
        if (stubs.Count <= 1)
            return false;

        var nodes = stubs
            .Select((stub, index) => ParseStubNode(stub, index))
            .ToList();

        var declared = nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.Name))
            .Select(n => n.Name!)
            .ToHashSet(StringComparer.Ordinal);

        if (declared.Count <= 1)
            return false;

        foreach (var node in nodes)
        {
            if (node.Dependencies.Count == 0)
                continue;

            node.Dependencies.RemoveWhere(d =>
                !declared.Contains(d) || string.Equals(d, node.Name, StringComparison.Ordinal));
        }

        var remaining = new List<StubNode>(nodes);
        var ordered = new List<StubNode>(nodes.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            var progressed = false;

            for (var i = 0; i < remaining.Count; i++)
            {
                var node = remaining[i];
                if (!node.Dependencies.All(emitted.Contains))
                    continue;

                ordered.Add(node);
                if (!string.IsNullOrWhiteSpace(node.Name))
                    emitted.Add(node.Name!);
                remaining.RemoveAt(i);
                i--;
                progressed = true;
            }

            if (progressed)
                continue;

            // Cycle or unresolved dependency chain among stubs: preserve original ordering for the rest.
            ordered.AddRange(remaining);
            break;
        }

        var reordered = ordered.Select(n => n.Text).ToList();
        if (reordered.SequenceEqual(stubs, StringComparer.Ordinal))
            return false;

        stubs.Clear();
        stubs.AddRange(reordered);
        return true;
    }

    private static StubNode ParseStubNode(string stub, int index)
    {
        var normalized = stub.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return new StubNode(index, normalized, null, []);

        var statement = SyntaxFactory.ParseStatement(normalized.TrimEnd(';') + ";");
        if (statement is not LocalDeclarationStatementSyntax localDecl
            || localDecl.Declaration.Variables.Count != 1)
        {
            return new StubNode(index, normalized, null, []);
        }

        var variable = localDecl.Declaration.Variables[0];
        var name = variable.Identifier.ValueText;
        var deps = variable.Initializer?.Value is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : variable.Initializer.Value
                .DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Select(id => id.Identifier.ValueText)
                .Where(id => !string.Equals(id, name, StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);

        return new StubNode(index, normalized, name, deps);
    }

    private sealed record StubNode(
        int Index,
        string Text,
        string? Name,
        HashSet<string> Dependencies);

}
