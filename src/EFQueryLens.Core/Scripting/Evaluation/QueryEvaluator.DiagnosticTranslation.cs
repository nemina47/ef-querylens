using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    /// <summary>
    /// Formats hard (non-retryable) diagnostics. Includes the CS code so the user can search for it.
    /// </summary>
    private static string FormatHardDiagnostics(IEnumerable<Diagnostic> errors) =>
        string.Join("; ", errors.Select(d => $"{d.Id}: {d.GetMessage()}"));

    /// <summary>
    /// Translates soft (retryable) diagnostics that have exhausted all retries into
    /// actionable, user-readable messages. Deduplicates repeated hints.
    /// </summary>
    private static string FormatSoftDiagnostics(IEnumerable<Diagnostic> errors)
    {
        var messages = errors
            .Select(d => d.Id switch
            {
                "CS0103" => TranslateCS0103(d),
                "CS0246" or "CS0234" or "CS0400" => TranslateCS0246(d),
                "CS1061" or "CS1929" => "Extension method or member not in scope — check your using directives.",
                "CS7036" => "Missing required argument — ensure all method parameters are provided.",
                "CS0019" => "Operator cannot be applied to the operand types — check variable types match the expression.",
                _ => $"{d.Id}: {d.GetMessage()}",
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return string.Join("; ", messages);
    }

    // "The name 'identifier' does not exist in the current context"
    private static readonly Regex _cs0103Pattern =
        new(@"The name '(.+?)' does not exist", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "The type or namespace name 'X' could not be found (are you missing a using directive or an assembly reference?)"
    private static readonly Regex _cs0246Pattern =
        new(@"The type or namespace name '(.+?)' could not be found", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string TranslateCS0103(Diagnostic d)
    {
        var m = _cs0103Pattern.Match(d.GetMessage());
        var name = m.Success ? m.Groups[1].Value : "?";
        return $"Unknown variable '{name}'. Add a local before the query, e.g.: var {name} = default;";
    }

    private static string TranslateCS0246(Diagnostic d)
    {
        var m = _cs0246Pattern.Match(d.GetMessage());
        var name = m.Success ? m.Groups[1].Value : "?";
        return $"Type '{name}' not found — add the required using directive or NuGet package reference.";
    }
}
