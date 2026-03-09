namespace EFQueryLens.Core.AssemblyContext;

/// <summary>
/// Creates <see cref="ProjectAssemblyContext"/> instances.
/// Does NOT cache — callers (engine, CLI) are responsible for deciding when
/// to reuse or discard contexts based on assembly timestamps.
/// </summary>
public static class ProjectAssemblyContextFactory
{
    /// <summary>
    /// Creates a new isolated <see cref="ProjectAssemblyContext"/> for the
    /// given assembly. Call <see cref="ProjectAssemblyContext.AssemblyTimestamp"/>
    /// to detect stale contexts after a rebuild.
    /// </summary>
    /// <param name="assemblyPath">Absolute path to the compiled .dll.</param>
    public static ProjectAssemblyContext Create(string assemblyPath) =>
        new(assemblyPath);

    /// <summary>
    /// Returns true if the assembly on disk has been modified since the context
    /// was created — i.e., the project has been rebuilt and the context is stale.
    /// </summary>
    public static bool IsStale(ProjectAssemblyContext context) =>
        File.GetLastWriteTimeUtc(context.AssemblyPath) != context.AssemblyTimestamp;
}
