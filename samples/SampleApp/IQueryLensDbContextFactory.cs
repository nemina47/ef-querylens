// ─────────────────────────────────────────────────────────────────────────────
// QueryLens interface stub
//
// This is a LOCAL copy of EFQueryLens.Core.IQueryLensDbContextFactory<T> placed
// in the EFQueryLens.Core namespace. No package reference to EFQueryLens.Core is
// needed — QueryLens discovers factories by full interface name via reflection
// and does not care which assembly the interface definition lives in.
//
// Keeping it here avoids assembly-loading conflicts that arise when QueryLens
// loads SampleApp into an isolated AssemblyLoadContext: if SampleApp referenced
// EFQueryLens.Core directly, the ALC would try to load EFQueryLens.Core.dll a
// second time (outside the default ALC), causing ReflectionTypeLoadException.
// ─────────────────────────────────────────────────────────────────────────────
// ReSharper disable once CheckNamespace
namespace EFQueryLens.Core;

/// <summary>
/// Implement this to give QueryLens a fully-configured offline
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> for SQL preview.
/// </summary>
public interface IQueryLensDbContextFactory<out TContext>
    where TContext : Microsoft.EntityFrameworkCore.DbContext
{
    /// <summary>Creates an offline context — no real DB connection needed.</summary>
    TContext CreateOfflineContext();
}
