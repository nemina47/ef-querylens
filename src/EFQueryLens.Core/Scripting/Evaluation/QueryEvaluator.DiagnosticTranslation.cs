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
                "CS1503" => TranslateCS1503(d),
                _ => $"{d.Id}: {d.GetMessage()}",
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return string.Join("; ", messages);
    }

    // "The name 'identifier' does not exist in the current context"
    private static readonly Regex _cs0103Pattern =
        new(@"The name '(.+?)' does not exist", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Matches both:
    //  - "The type or namespace name 'X' could not be found ..." (CS0246/CS0400)
    //  - "The type or namespace name 'X' does not exist in the namespace 'Y' ..." (CS0234)
    private static readonly Regex _typeOrNamespaceNamePattern =
        new(@"The type or namespace name '(.+?)' (could not be found|does not exist)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string TranslateCS0103(Diagnostic d)
    {
        var m = _cs0103Pattern.Match(d.GetMessage());
        var name = m.Success ? m.Groups[1].Value : "?";
        return $"Unknown variable '{name}'. Add a local before the query, e.g.: var {name} = default;";
    }

    private static string TranslateCS0246(Diagnostic d)
    {
        var name = TryExtractTypeNameFromCS0246(d);
        if (string.IsNullOrWhiteSpace(name))
            return $"{d.Id}: {d.GetMessage()}";

        return $"Type '{name}' not found — add the required using directive or NuGet package reference.";
    }

    /// <summary>
    /// Extracts the simple type name from a CS0246 diagnostic message, e.g.
    /// "The type or namespace name 'CustomerRevenueDto' could not be found…" → "CustomerRevenueDto".
    /// Returns <see langword="null"/> if the pattern does not match.
    /// </summary>
    internal static string? TryExtractTypeNameFromCS0246(Diagnostic d)
    {
        var m = _typeOrNamespaceNamePattern.Match(d.GetMessage());
        return m.Success ? m.Groups[1].Value : null;
    }

    // "Argument N: cannot convert from 'actualType' to 'expectedType'"
    private static readonly Regex _cs1503Pattern =
        new(@"cannot convert from '.+?' to '(.+?)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string TranslateCS1503(Diagnostic d)
    {
        var m = _cs1503Pattern.Match(d.GetMessage());
        var expected = m.Success ? m.Groups[1].Value : "?";
        return $"Argument type mismatch — a captured local variable was stubbed as 'object' but '{expected}' is required. " +
               "Ensure the variable is declared with a concrete type before the query.";
    }

    /// <summary>
    /// Extracts the expected (target) type from a CS1503 diagnostic message, e.g.
    /// "Argument 3: cannot convert from 'object' to 'string?'" → "string?".
    /// Returns <see langword="null"/> if the pattern does not match.
    /// </summary>
    internal static string? TryExtractExpectedTypeFromCS1503(Diagnostic d)
    {
        var m = _cs1503Pattern.Match(d.GetMessage());
        return m.Success ? m.Groups[1].Value : null;
    }
}
