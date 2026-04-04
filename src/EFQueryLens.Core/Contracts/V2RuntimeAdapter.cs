/// <summary>
/// V2 Runtime Adapter - bridges v2 extraction and capture plans into runtime evaluation decisions.
/// Provides deterministic diagnostics for rejected or unsupported v2 shapes.
/// Used by QueryEvaluator to make execution decisions based on v2 IR, deferring full codegen to future slices.
/// </summary>
namespace EFQueryLens.Core.Contracts;

/// <summary>
/// Runtime result of v2 payload validation and decision-making.
/// Determines whether the runtime should proceed with v2 execution, fall back to legacy, or fail with diagnostics.
/// </summary>
public sealed record V2RuntimeDecision
{
    /// <summary>
    /// True if v2 payloads are present, valid, and the runtime should attempt v2 execution path.
    /// </summary>
    public required bool ShouldUseV2Path { get; init; }

    /// <summary>
    /// Reason code if v2 execution should not proceed (e.g., "rejected-capture", "invalid-extraction-state").
    /// When non-empty, runtime should fail with explicit diagnostic instead of silent legacy fallback.
    /// </summary>
    public string? BlockReason { get; init; }

    /// <summary>
    /// Human-readable message explaining the v2 execution block reason.
    /// </summary>
    public string? BlockMessage { get; init; }

    /// <summary>
    /// Snapshot of v2 extraction plan (if valid).
    /// </summary>
    public V2QueryExtractionPlanSnapshot? ExtractionPlan { get; init; }

    /// <summary>
    /// Snapshot of v2 capture plan (if valid).
    /// </summary>
    public V2CapturePlanSnapshot? CapturePlan { get; init; }
}

/// <summary>
/// Analyzer for v2 payloads - determines runtime execution path and diagnostics.
/// </summary>
public static class V2RuntimeAnalyzer
{
    /// <summary>
    /// Analyze v2 payloads in the translation request and determine runtime behavior.
    /// Returns a decision indicating whether to use v2 path, fall back, or fail.
    /// </summary>
    public static V2RuntimeDecision Analyze(TranslationRequest request)
    {
        // No v2 payloads - proceed with legacy path
        if (request.V2ExtractionPlan is null && request.V2CapturePlan is null)
        {
            return new V2RuntimeDecision { ShouldUseV2Path = false };
        }

        // Partial v2 state - extraction without capture
        if (request.V2ExtractionPlan is not null && request.V2CapturePlan is null)
        {
            return new V2RuntimeDecision
            {
                ShouldUseV2Path = false,
                BlockReason = "incomplete-v2-state",
                BlockMessage = "V2 extraction plan present but capture plan missing. Runtime requires both for deterministic execution.",
            };
        }

        // Capture plan present - check validity
        if (request.V2CapturePlan is not null)
        {
            // If capture has diagnostics, it was explicitly rejected
            if (request.V2CapturePlan.Diagnostics.Count > 0)
            {
                var topDiag = request.V2CapturePlan.Diagnostics.First();
                return new V2RuntimeDecision
                {
                    ShouldUseV2Path = false,
                    BlockReason = $"capture-rejected:{topDiag.Code}",
                    BlockMessage = $"Capture plan rejected during LSP analysis: {topDiag.Message}",
                    CapturePlan = request.V2CapturePlan,
                };
            }

            // Capture is complete and valid - use v2 path
            if (request.V2CapturePlan.IsComplete)
            {
                return new V2RuntimeDecision
                {
                    ShouldUseV2Path = true,
                    ExtractionPlan = request.V2ExtractionPlan,
                    CapturePlan = request.V2CapturePlan,
                };
            }

            // Capture incomplete
            return new V2RuntimeDecision
            {
                ShouldUseV2Path = false,
                BlockReason = "incomplete-capture-plan",
                BlockMessage = "V2 capture plan is incomplete. Runtime cannot proceed with deterministic execution.",
                CapturePlan = request.V2CapturePlan,
            };
        }

        // Extraction plan only (no capture) - cannot execute deterministically
        return new V2RuntimeDecision
        {
            ShouldUseV2Path = false,
            BlockReason = "extraction-only-no-capture",
            BlockMessage = "V2 extraction plan present but capture plan missing. Runtime requires capture plan for deterministic variable handling.",
            ExtractionPlan = request.V2ExtractionPlan,
        };
    }

    /// <summary>
    /// Check if a capture plan entry indicates the symbol should use replay initialization.
    /// </summary>
    public static bool IsReplayInitializer(V2CapturePlanEntry entry)
    {
        return string.Equals(entry.CapturePolicy, LocalSymbolReplayPolicies.ReplayInitializer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Check if a capture plan entry indicates the symbol should use placeholder.
    /// </summary>
    public static bool IsPlaceholder(V2CapturePlanEntry entry)
    {
        return string.Equals(entry.CapturePolicy, LocalSymbolReplayPolicies.UsePlaceholder, StringComparison.Ordinal);
    }

    /// <summary>
    /// Check if a capture plan entry was rejected.
    /// </summary>
    public static bool IsRejected(V2CapturePlanEntry entry)
    {
        return string.Equals(entry.CapturePolicy, LocalSymbolReplayPolicies.Reject, StringComparison.Ordinal);
    }

    /// <summary>
    /// Format v2 diagnostic for user display.
    /// </summary>
    public static string FormatDiagnostic(V2RuntimeDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.BlockReason))
            return "(no v2 diagnostic)";

        return $"{decision.BlockReason}: {decision.BlockMessage}";
    }
}
