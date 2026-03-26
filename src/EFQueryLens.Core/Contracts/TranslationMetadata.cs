namespace EFQueryLens.Core.Contracts;

public sealed record TranslationMetadata
{
    public string DbContextType { get; init; } = null!;
    public string EfCoreVersion { get; init; } = null!;
    public string ProviderName { get; init; } = null!;
    public TimeSpan TranslationTime { get; init; }
    public TimeSpan? ContextResolutionTime { get; init; }
    public TimeSpan? DbContextCreationTime { get; init; }
    public TimeSpan? MetadataReferenceBuildTime { get; init; }
    public TimeSpan? RoslynCompilationTime { get; init; }
    public int? CompilationRetryCount { get; init; }
    public TimeSpan? EvalAssemblyLoadTime { get; init; }
    public TimeSpan? RunnerExecutionTime { get; init; }

    /// <summary>
    /// True when EF Core silently evaluated part of the query on the client.
    /// Always flag this — it is a silent performance killer.
    /// </summary>
    public bool HasClientEvaluation { get; init; }

    /// <summary>
    /// How the offline DbContext instance was created.
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>"ef-design-time-factory"</c> — an
    ///     <c>IDesignTimeDbContextFactory&lt;T&gt;</c> created the DbContext.
    ///   </description></item>
    ///   <item><description>
    ///     <c>"pooled-reuse"</c> — a previously-created DbContext instance
    ///     was reused from the pool.
    ///   </description></item>
    /// </list>
    /// </summary>
    public string CreationStrategy { get; init; } = "ef-design-time-factory";
}
