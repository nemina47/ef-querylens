namespace EFQueryLens.Core;

/// <summary>
/// Globals type injected into the Roslyn scripting sandbox.
/// </summary>
/// <remarks>
/// The raw DbContext is exposed under a deliberately unusual name so that it
/// cannot collide with the user's requested variable (e.g. "db").  The preamble
/// immediately creates a strongly-typed local alias:
/// <code>
///   var db = (SampleApp.AppDbContext)(object)__ql_raw_ctx__;
/// </code>
/// Using a typed local rather than the dynamic global ensures that user
/// expressions compile cleanly (no CS1977, no DLR-dispatched Enumerable.Where).
/// The DbContext instance originates from the default ALC so that the cast
/// always succeeds at runtime.
/// </remarks>
public sealed class QueryScriptGlobals
{
    // ReSharper disable once InconsistentNaming
    public dynamic __ql_raw_ctx__ { get; set; } = default!;
}
