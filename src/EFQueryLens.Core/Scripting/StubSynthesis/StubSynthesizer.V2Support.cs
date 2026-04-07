// StubSynthesizer.V2Support.cs — adapter that converts a V2CapturePlanSnapshot into the
// List<string> stub declarations consumed by TryBuildCompilationWithRetries. Bridges the
// v2 capture-plan codegen path into the existing Roslyn compilation pipeline without
// changing any downstream method signatures.
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Scripting.Compilation;

namespace EFQueryLens.Core.Scripting.Evaluation;

internal static partial class StubSynthesizer
{
    /// <summary>
    /// Builds stub declarations from a v2 capture plan, replacing the legacy
    /// <see cref="BuildInitialStubs"/> path when <c>V2RuntimeDecision.ShouldUseV2Path</c> is true.
    /// </summary>
    /// <remarks>
    /// Each entry is converted via <see cref="EvalSourceBuilder.BuildV2CaptureInitializationCode"/>.
    /// Entries with <c>Reject</c> policy return null and are excluded from the stub list.
    /// </remarks>
    internal static List<string> BuildV2Stubs(
        V2CapturePlanSnapshot capturePlan,
        string executableExpression,
        string contextVariableName)
    {
        var stubs = new List<string>();

        var entries = capturePlan?.Entries ?? [];
        foreach (var entry in entries)
        {
            var stub = global::EFQueryLens.Core.Scripting.Compilation.EvalSourceBuilder.BuildV2CaptureInitializationCode(entry);
            if (stub is not null)
                stubs.Add(stub);
        }

        // Factory-root substitution rewrites receiver to __qlFactoryContext.*.
        // Ensure generated source always has that identifier bound to the runtime context variable.
        if (!string.IsNullOrWhiteSpace(executableExpression)
            && executableExpression.Contains("__qlFactoryContext", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(contextVariableName)
            && !stubs.Any(s => s.Contains("__qlFactoryContext", StringComparison.Ordinal)))
        {
            stubs.Insert(0, $"var __qlFactoryContext = {contextVariableName};");
        }

        return stubs;
    }
}
