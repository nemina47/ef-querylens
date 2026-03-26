namespace EFQueryLens.Core.Tests;

/// <summary>
/// Tests that exercise collectible AssemblyLoadContext + cross-project runtime loading
/// can interfere when executed concurrently in the same test host process.
/// Keep them in a dedicated non-parallelized collection for deterministic results.
/// </summary>
[CollectionDefinition("AssemblyLoadContextIsolation", DisableParallelization = true)]
public sealed class AssemblyLoadContextIsolationCollection
{
}

[CollectionDefinition("QueryEvaluatorIsolation", DisableParallelization = true)]
public sealed class QueryEvaluatorIsolationCollection
{
}

[CollectionDefinition("ProjectAssemblyContextIsolation", DisableParallelization = true)]
public sealed class ProjectAssemblyContextIsolationCollection
{
}
