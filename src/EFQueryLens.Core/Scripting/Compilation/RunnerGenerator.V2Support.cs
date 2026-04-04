using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFQueryLens.Core.Scripting.Compilation;

/// <summary>
/// V2 capture-plan support for RunnerGenerator.
/// Extends runner code generation to emit v2-aware initialization for captured symbols
/// based on capture-plan policies (replay, placeholder, reject).
/// </summary>
internal static partial class RunnerGenerator
{
    /// <summary>
    /// Builds initialization statements for a v2 capture plan.
    /// Converts capture-plan entries into C# variable declarations with appropriate init values.
    /// </summary>
    internal static IReadOnlyList<StatementSyntax> BuildV2CapturePlanInitialization(
        V2CapturePlanSnapshot capturePlan)
    {
        var statements = new List<StatementSyntax>();

        if (capturePlan?.Entries == null || capturePlan.Entries.Count == 0)
            return statements;

        foreach (var entry in capturePlan.Entries)
        {
            var initCode = EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);
            if (initCode == null)
            {
                // Reject policy - skip this entry
                continue;
            }

            // Parse the generated code into a statement
            try
            {
                var statement = SyntaxFactory.ParseStatement(initCode);
                statements.Add(statement);
            }
            catch
            {
                // If parsing fails, skip this entry to avoid breaking codegen
                continue;
            }
        }

        return statements;
    }

    /// <summary>
    /// Determines if a v2 capture plan is eligible for v2 codegen.
    /// A capture plan is eligible if it is complete and has no diagnostics.
    /// </summary>
    internal static bool IsV2CapturePlanEligible(V2CapturePlanSnapshot? capturePlan)
    {
        if (capturePlan == null)
            return false;

        // Must be marked complete by capture analysis
        if (!capturePlan.IsComplete)
            return false;

        // Must have no diagnostics indicating rejections
        if (capturePlan.Diagnostics?.Count > 0)
            return false;

        return true;
    }

    /// <summary>
    /// Counts how many capture-plan entries will actually generate initialization code.
    /// Entries with Reject policy are excluded.
    /// </summary>
    internal static int CountV2ExecutableEntries(V2CapturePlanSnapshot? capturePlan)
    {
        if (capturePlan?.Entries == null)
            return 0;

        return capturePlan.Entries.Count(e => 
            e.CapturePolicy != LocalSymbolReplayPolicies.Reject);
    }
}
