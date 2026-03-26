namespace EFQueryLens.Core.Scripting.DesignTime;

/// <summary>
/// Pure-reflection helpers that discover and invoke QueryLens factory interfaces
/// in the user's assemblies, without requiring direct package references.
/// </summary>
internal static partial class DesignTimeDbContextFactory
{
    private const string QueryLensInterfaceName =
        "EFQueryLens.Core.Contracts.IQueryLensDbContextFactory`1";
}
