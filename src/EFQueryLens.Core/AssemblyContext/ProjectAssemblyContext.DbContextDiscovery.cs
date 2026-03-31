using System.Reflection;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Core.AssemblyContext;

public sealed partial class ProjectAssemblyContext
{
    /// <summary>
    /// Returns all concrete (non-abstract) DbContext subclasses found across
    /// <b>all assemblies</b> currently loaded into this context (including any
    /// additional assemblies preloaded via <see cref="LoadAdditionalAssembly"/>).
    /// Walks the full inheritance chain by type name, because the DbContext type
    /// in the user's ALC is a different runtime instance than the one in the
    /// tool's default load context.
    /// </summary>
    public IReadOnlyList<Type> FindDbContextTypes()
    {
        EnsureNotDisposed();

        var results = new List<Type>();

        // Scan ALL loaded assemblies - not just the primary target - so that
        // projects which place DbContext in a class library dependency are
        // discovered correctly after LoadAdditionalAssembly() is called.
        foreach (var asm in LoadedAssemblies)
        {
            IEnumerable<Type> candidates;
            try
            {
                candidates = asm.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                // Some types in the assembly couldn't be loaded (e.g. a factory
                // class that references a design-time interface whose assembly is
                // not in the probe paths). The successfully loaded types are still
                // in rtle.Types - use them so that DbContext types aren't missed.
                candidates = rtle.Types.Where(t => t is not null)!;
            }
            catch
            {
                // Truly broken assembly - skip entirely.
                continue;
            }

            foreach (var type in candidates)
            {
                try
                {
                    if (!type.IsAbstract && IsDbContextSubclass(type))
                        results.Add(type);
                }
                catch
                {
                    // IsDbContextSubclass may throw for partially-loaded types.
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Resolves a single DbContext type from the loaded assembly.
    /// </summary>
    /// <param name="typeName">
    ///   Simple name ("AppDbContext") or fully qualified name
    ///   ("SampleApp.AppDbContext"). Pass null to auto-discover when exactly
    ///   one DbContext exists in the assembly.
    /// </param>
    /// <param name="expressionHint">
    ///   Optional query-expression hint used to disambiguate DbContext candidates
    ///   based on the first accessed DbSet-like property.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///   No DbContext found; multiple found with null typeName; or no match
    ///   for the provided typeName.
    /// </exception>
    public Type FindDbContextType(
        string? typeName = null,
        string? expressionHint = null,
        DbContextResolutionSnapshot? resolutionSnapshot = null)
    {
        EnsureNotDisposed();

        var all = FindDbContextTypes();
        var assemblyFileName = Path.GetFileName(AssemblyPath);

        if (all.Count == 0)
            throw new DbContextDiscoveryException(
                DbContextDiscoveryFailureKind.NoDbContextFound,
                $"No DbContext subclass found in '{assemblyFileName}'.");

        var dbSetName = expressionHint is null ? null : ExtractFirstPropertyAccess(expressionHint);
        var expressionMatches = string.IsNullOrWhiteSpace(dbSetName)
            ? []
            : all.Where(t => OwnsProperty(t, dbSetName)).ToList();

        var explicitMatches = MatchDbContextCandidates(all, typeName);
        var declaredMatches = MatchDbContextCandidates(all, resolutionSnapshot?.DeclaredTypeName);
        var factoryMatches = MatchDbContextCandidates(all, resolutionSnapshot?.FactoryTypeName);
        var factoryCandidateMatches = MatchDbContextCandidates(all, resolutionSnapshot?.FactoryCandidateTypeNames);

        var resolved = TryResolveDeclaredFactoryConflict(
            expressionMatches,
            declaredMatches,
            factoryMatches,
            assemblyFileName);
        if (resolved is not null)
            return resolved;

        resolved = TryResolveFactoryHint(expressionMatches, factoryMatches, dbSetName);
        if (resolved is not null)
            return resolved;

        resolved = TryResolveExplicitHint(typeName, expressionMatches, explicitMatches, assemblyFileName, dbSetName);
        if (resolved is not null)
            return resolved;

        resolved = TryResolveDeclaredHint(expressionMatches, declaredMatches, factoryCandidateMatches, resolutionSnapshot, assemblyFileName);
        if (resolved is not null)
            return resolved;

        resolved = TryResolveFactoryCandidateHint(expressionMatches, factoryCandidateMatches, assemblyFileName);
        if (resolved is not null)
            return resolved;

        if (typeName is null)
            return ResolveAutoDiscovery(all, expressionMatches, assemblyFileName);

        throw new InvalidOperationException(
            $"DbContext type '{typeName}' not found in '{assemblyFileName}'. " +
            $"Available: {string.Join(", ", all.Select(t => t.FullName))}");
    }

    private static Type? TryResolveDeclaredFactoryConflict(
        IReadOnlyList<Type> expressionMatches,
        IReadOnlyList<Type> declaredMatches,
        IReadOnlyList<Type> factoryMatches,
        string assemblyFileName)
    {
        if (factoryMatches.Count != 1 || declaredMatches.Count != 1 || factoryMatches[0] == declaredMatches[0])
            return null;

        if (expressionMatches.Count == 1 && (expressionMatches[0] == factoryMatches[0] || expressionMatches[0] == declaredMatches[0]))
            return expressionMatches[0];

        throw new DbContextDiscoveryException(
            DbContextDiscoveryFailureKind.ConflictingDbContextHints,
            $"Conflicting DbContext hints for '{assemblyFileName}': declared='{declaredMatches[0].FullName}', factory='{factoryMatches[0].FullName}'. " +
            "Rebuild the selected host project or specify a concrete DbContext explicitly.");
    }

    private static Type? TryResolveFactoryHint(
        IReadOnlyList<Type> expressionMatches,
        IReadOnlyList<Type> factoryMatches,
        string? dbSetName)
    {
        if (factoryMatches.Count != 1)
            return null;

        var factoryMatch = factoryMatches[0];
        if (expressionMatches.Count == 1 && expressionMatches[0] != factoryMatch)
        {
            throw new DbContextDiscoveryException(
                DbContextDiscoveryFailureKind.ConflictingDbContextHints,
                $"Resolved DbContext '{factoryMatch.FullName}' from QueryLens factory, but expression root '{dbSetName}' belongs to '{expressionMatches[0].FullName}'. " +
                "Check the selected host assembly and QueryLens factory configuration.");
        }

        return factoryMatch;
    }

    private static Type? TryResolveExplicitHint(
        string? typeName,
        IReadOnlyList<Type> expressionMatches,
        IReadOnlyList<Type> explicitMatches,
        string assemblyFileName,
        string? dbSetName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        if (explicitMatches.Count == 1)
        {
            var explicitMatch = explicitMatches[0];
            if (expressionMatches.Count == 1 && expressionMatches[0] != explicitMatch)
            {
                throw new DbContextDiscoveryException(
                    DbContextDiscoveryFailureKind.ConflictingDbContextHints,
                    $"Requested DbContext '{explicitMatch.FullName}' does not expose '{dbSetName}', which matches '{expressionMatches[0].FullName}'. " +
                    "Specify the concrete DbContext that owns the queried DbSet.");
            }

            return explicitMatch;
        }

        if (explicitMatches.Count > 1)
        {
            if (expressionMatches.Count == 1 && explicitMatches.Contains(expressionMatches[0]))
                return expressionMatches[0];

            throw new DbContextDiscoveryException(
                DbContextDiscoveryFailureKind.MultipleDbContextsFound,
                $"Multiple DbContext types match '{typeName}' in '{assemblyFileName}': " +
                $"{string.Join(", ", explicitMatches.Select(t => t.FullName))}. Specify a concrete fully qualified DbContext type name.");
        }

        return null;
    }

    private static Type? TryResolveDeclaredHint(
        IReadOnlyList<Type> expressionMatches,
        IReadOnlyList<Type> declaredMatches,
        IReadOnlyList<Type> factoryCandidateMatches,
        DbContextResolutionSnapshot? resolutionSnapshot,
        string assemblyFileName)
    {
        if (declaredMatches.Count == 1)
        {
            var declaredMatch = declaredMatches[0];
            if (expressionMatches.Count == 1 && expressionMatches[0] != declaredMatch && factoryCandidateMatches.Count > 1 && factoryCandidateMatches.Contains(expressionMatches[0]))
                return expressionMatches[0];

            return declaredMatch;
        }

        if (declaredMatches.Count > 1)
        {
            if (expressionMatches.Count == 1 && declaredMatches.Contains(expressionMatches[0]))
                return expressionMatches[0];

            throw new DbContextDiscoveryException(
                DbContextDiscoveryFailureKind.MultipleDbContextsFound,
                $"Multiple DbContext types match declared hint '{resolutionSnapshot?.DeclaredTypeName}' in '{assemblyFileName}': " +
                $"{string.Join(", ", declaredMatches.Select(t => t.FullName))}. Specify a concrete fully qualified DbContext type name.");
        }

        return null;
    }

    private static Type? TryResolveFactoryCandidateHint(
        IReadOnlyList<Type> expressionMatches,
        IReadOnlyList<Type> factoryCandidateMatches,
        string assemblyFileName)
    {
        if (factoryCandidateMatches.Count == 1)
            return factoryCandidateMatches[0];

        if (factoryCandidateMatches.Count > 1)
        {
            if (expressionMatches.Count == 1 && factoryCandidateMatches.Contains(expressionMatches[0]))
                return expressionMatches[0];

            throw new DbContextDiscoveryException(
                DbContextDiscoveryFailureKind.MultipleDbContextsFound,
                $"Multiple QueryLens factory DbContext candidates found in '{assemblyFileName}': " +
                $"{string.Join(", ", factoryCandidateMatches.Select(t => t.FullName))}. " +
                "Hover a query that references a DbSet unique to the intended DbContext or specify a concrete DbContext type.");
        }

        return null;
    }

    private static Type ResolveAutoDiscovery(
        IReadOnlyList<Type> all,
        IReadOnlyList<Type> expressionMatches,
        string assemblyFileName)
    {
        if (all.Count == 1)
            return all[0];

        // Auto-disambiguate using the LINQ expression root property when exactly one
        // DbContext owns the referenced DbSet/IQueryable property.
        if (expressionMatches.Count == 1)
            return expressionMatches[0];

        // Fallback: filter out obvious test/utility DbContexts.
        var filtered = FilterOutUtilityDbContexts(all);

        if (filtered.Count == 1)
            return filtered[0];

        var candidates = filtered.Count > 1 ? filtered : all;
        throw new DbContextDiscoveryException(
            DbContextDiscoveryFailureKind.MultipleDbContextsFound,
            $"Multiple DbContext types found in '{assemblyFileName}': " +
            $"{string.Join(", ", candidates.Select(t => t.FullName))}. " +
            "Specify --context to disambiguate.");
    }

    private static List<Type> FilterOutUtilityDbContexts(IReadOnlyList<Type> candidates)
    {
        return candidates
            .Where(static t =>
            {
                var name = t.Name;
                return !name.Contains("Test", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Empty", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Mock", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }

    /// <summary>
    /// Extracts the first member access from a LINQ expression.
    /// e.g. "dbContext.AppWorkflows.Include(...)" -> "AppWorkflows"
    ///      "db.Orders.Where(...)" -> "Orders"
    /// </summary>
    private static string? ExtractFirstPropertyAccess(string expression)
    {
        var tree = CSharpSyntaxTree.ParseText(
            expression,
            CSharpParseOptions.Default.WithKind(SourceCodeKind.Script));

        var root = tree.GetRoot();

        var memberAccess = root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .FirstOrDefault(static m => IsRootContextExpression(m.Expression));
        if (memberAccess is not null)
        {
            return memberAccess.Name.Identifier.Text;
        }

        var conditionalAccess = root.DescendantNodes()
            .OfType<ConditionalAccessExpressionSyntax>()
            .FirstOrDefault(static c => IsRootContextExpression(c.Expression));
        if (conditionalAccess?.WhenNotNull is MemberBindingExpressionSyntax memberBinding)
        {
            return memberBinding.Name.Identifier.Text;
        }

        var fallbackMemberBinding = root.DescendantNodes()
            .OfType<MemberBindingExpressionSyntax>()
            .FirstOrDefault();
        return fallbackMemberBinding?.Name.Identifier.Text;

        static bool IsRootContextExpression(ExpressionSyntax expression) => expression switch
        {
            IdentifierNameSyntax => true,
            ParenthesizedExpressionSyntax { Expression: IdentifierNameSyntax } => true,
            _ => false,
        };
    }

    private static List<Type> MatchDbContextCandidates(IReadOnlyList<Type> all, string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return [];

        var normalizedHint = hint.TrimEnd('?');

        var exactFullName = all
            .Where(t => string.Equals(t.FullName, normalizedHint, StringComparison.Ordinal))
            .ToList();
        if (exactFullName.Count > 0)
            return exactFullName;

        var exactSimpleName = all
            .Where(t => string.Equals(t.Name, normalizedHint, StringComparison.Ordinal))
            .ToList();
        if (exactSimpleName.Count > 0)
            return exactSimpleName;

        var interfaceFullName = all.Where(t =>
                t.GetInterfaces().Any(i => string.Equals(i.FullName, normalizedHint, StringComparison.Ordinal)))
            .ToList();
        if (interfaceFullName.Count > 0)
            return interfaceFullName;

        return all.Where(t =>
                t.GetInterfaces().Any(i => string.Equals(i.Name, normalizedHint, StringComparison.Ordinal)))
            .ToList();
    }

    private static List<Type> MatchDbContextCandidates(IReadOnlyList<Type> all, IReadOnlyList<string>? hints)
    {
        if (hints is null || hints.Count == 0)
            return [];

        return hints
            .Where(static hint => !string.IsNullOrWhiteSpace(hint))
            .SelectMany(hint => MatchDbContextCandidates(all, hint))
            .Distinct()
            .ToList();
    }

    private static bool OwnsProperty(Type dbContextType, string propertyName)
    {
        try
        {
            return dbContextType.GetProperties().Any(p => string.Equals(p.Name, propertyName, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Walks the base-type chain by FullName to detect DbContext subclasses.
    /// Name-based comparison is required because the DbContext type loaded
    /// inside the ALC is a different runtime type than typeof(DbContext) in
    /// the host process - even though they are semantically the same class.
    /// </summary>
    private static bool IsDbContextSubclass(Type type)
    {
        const string dbContextFullName = "Microsoft.EntityFrameworkCore.DbContext";
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.FullName == dbContextFullName)
                return true;
            current = current.BaseType;
        }

        return false;
    }
}
