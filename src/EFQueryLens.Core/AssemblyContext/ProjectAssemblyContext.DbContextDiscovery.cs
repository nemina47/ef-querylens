using System.Reflection;

namespace EFQueryLens.Core.AssemblyContext;

public sealed partial class ProjectAssemblyContext
{
    /// <summary>
    /// Returns all concrete (non-abstract) DbContext subclasses found across
    /// <b>all assemblies</b> currently loaded into this context (including any
    /// additional assemblies pre-loaded via <see cref="LoadAdditionalAssembly"/>).
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
    /// <exception cref="InvalidOperationException">
    ///   No DbContext found; multiple found with null typeName; or no match
    ///   for the provided typeName.
    /// </exception>
    public Type FindDbContextType(string? typeName = null, string? expressionHint = null)
    {
        EnsureNotDisposed();

        var all = FindDbContextTypes();

        if (all.Count == 0)
            throw new InvalidOperationException(
                $"No DbContext subclass found in '{Path.GetFileName(AssemblyPath)}'.");

        if (typeName is null)
        {
            if (all.Count == 1)
                return all[0];

            // Auto-disambiguate using the LINQ expression: extract the first property
            // access (e.g. "AppWorkflows" from "dbContext.AppWorkflows.Include(...)") and
            // find which DbContext owns a DbSet/IQueryable property with that name.
            if (expressionHint is not null)
            {
                var dbSetName = ExtractFirstPropertyAccess(expressionHint);
                if (dbSetName is not null)
                {
                    var match = all.FirstOrDefault(t =>
                        t.GetProperties().Any(p =>
                            string.Equals(p.Name, dbSetName, StringComparison.Ordinal)));

                    if (match is not null)
                        return match;
                }
            }

            // Fallback: filter out obvious test/utility DbContexts.
            var filtered = all.Where(t =>
            {
                var name = t.Name;
                return !name.Contains("Test", StringComparison.OrdinalIgnoreCase) &&
                       !name.Contains("Empty", StringComparison.OrdinalIgnoreCase) &&
                       !name.Contains("Mock", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (filtered.Count == 1)
                return filtered[0];

            var candidates = filtered.Count > 1 ? filtered : all;
            throw new InvalidOperationException(
                $"Multiple DbContext types found in '{Path.GetFileName(AssemblyPath)}': " +
                $"{string.Join(", ", candidates.Select(t => t.FullName))}. " +
                "Specify --context to disambiguate.");
        }

        // Match on simple name or fully-qualified name.
        var nameMatch = all.FirstOrDefault(t =>
            string.Equals(t.Name, typeName, StringComparison.Ordinal) ||
            string.Equals(t.FullName, typeName, StringComparison.Ordinal));

        return nameMatch ?? throw new InvalidOperationException(
            $"DbContext type '{typeName}' not found in '{Path.GetFileName(AssemblyPath)}'. " +
            $"Available: {string.Join(", ", all.Select(t => t.FullName))}");
    }

    /// <summary>
    /// Extracts the first member access from a LINQ expression.
    /// e.g. "dbContext.AppWorkflows.Include(...)" -> "AppWorkflows"
    ///      "db.Orders.Where(...)" -> "Orders"
    /// </summary>
    private static string? ExtractFirstPropertyAccess(string expression)
    {
        // Trim leading whitespace and the variable name prefix (e.g. "dbContext." or "db.")
        var trimmed = expression.TrimStart();

        // Find the first dot - everything after is the property chain.
        var firstDot = trimmed.IndexOf('.');
        if (firstDot < 0 || firstDot >= trimmed.Length - 1)
            return null;

        var afterDot = trimmed[(firstDot + 1)..].TrimStart();

        // The property name is everything up to the next dot, paren, or whitespace.
        var endIndex = 0;
        while (endIndex < afterDot.Length &&
               char.IsLetterOrDigit(afterDot[endIndex]) || afterDot[endIndex] == '_')
        {
            endIndex++;
        }

        return endIndex > 0 ? afterDot[..endIndex] : null;
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
