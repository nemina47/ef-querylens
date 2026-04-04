using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Core.Scripting.Compilation;

/// <summary>
/// V2 capture-plan support for EvalSourceBuilder.
/// Interprets v2 capture-plan policies (ReplayInitializer, UsePlaceholder, Reject)
/// to drive code generation for symbol initialization.
/// </summary>
internal static partial class EvalSourceBuilder
{
    /// <summary>
    /// Builds initialization code for a v2 capture-plan entry based on capture policy.
    /// </summary>
    /// <para>
    /// Policy interpretation:
    /// - ReplayInitializer: Emit normal replay initialization code (status quo)
    /// - UsePlaceholder: Emit default/placeholder value for the symbol type
    /// - Reject: Symbol should not be initialized; capture will fail
    /// </para>
    internal static string? BuildV2CaptureInitializationCode(V2CapturePlanEntry entry)
    {
        return entry.CapturePolicy switch
        {
            LocalSymbolReplayPolicies.ReplayInitializer 
                => BuildReplayInitializerCode(entry),
            
            LocalSymbolReplayPolicies.UsePlaceholder 
                => BuildPlaceholderInitializationCode(entry),
            
            LocalSymbolReplayPolicies.Reject 
                => null,
            
            _ => null,
        };
    }

    /// <summary>
    /// Builds standard replay initializer code for a symbol.
    /// This is the current generation path for symbols that can be captured.
    /// </summary>
    private static string BuildReplayInitializerCode(V2CapturePlanEntry entry)
    {
        // Use the entry's InitializerExpression if provided, otherwise fall back to default
        if (!string.IsNullOrWhiteSpace(entry.InitializerExpression))
        {
            return $"var {entry.Name} = {entry.InitializerExpression};";
        }

        // Fallback: default(T) for the symbol's type
        return $"var {entry.Name} = default({entry.TypeName});";
    }

    /// <summary>
    /// Builds placeholder initialization code for a symbol.
    /// Placeholders are safe default values used when the actual symbol cannot be captured.
    /// </summary>
    private static string BuildPlaceholderInitializationCode(V2CapturePlanEntry entry)
    {
        // For reference types: null
        // For value types: default(T)
        // For strings: empty string
        // We default to default(T) which is safe for all types
        return $"var {entry.Name} = default({entry.TypeName});";
    }
}
