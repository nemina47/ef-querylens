namespace EFQueryLens.Core.AssemblyContext;

public enum DbContextDiscoveryFailureKind
{
    NoDbContextFound,
    MultipleDbContextsFound,
    ConflictingDbContextHints,
}

public sealed class DbContextDiscoveryException : InvalidOperationException
{
    public DbContextDiscoveryException(DbContextDiscoveryFailureKind failureKind, string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public DbContextDiscoveryFailureKind FailureKind { get; }
}
