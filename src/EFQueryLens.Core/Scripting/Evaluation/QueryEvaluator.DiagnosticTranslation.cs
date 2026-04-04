using System.Reflection;
using System.Text.RegularExpressions;
using EFQueryLens.Core.Contracts;
using Microsoft.CodeAnalysis;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    internal static QueryTranslationResult FailureFromDiagnostics(
        string stage,
        IEnumerable<Diagnostic> errors,
        TimeSpan elapsed,
        Type? dbContextType,
        IEnumerable<Assembly>? userAssemblies,
        bool softDiagnostics,
        string sourceDumpPath)
    {
        var diagnostics = errors.ToList();
        var message = stage + ": " + (softDiagnostics
            ? FormatSoftDiagnostics(diagnostics)
            : FormatHardDiagnostics(diagnostics));
        var detail = BuildDiagnosticDetail(diagnostics, sourceDumpPath);
        return Failure(message, elapsed, dbContextType, userAssemblies, diagnosticDetail: detail);
    }

    private static string BuildDiagnosticDetail(IEnumerable<Diagnostic> diagnostics, string sourceDumpPath)
    {
        var raw = string.Join("; ", diagnostics.Take(10).Select(d => $"{d.Id}: {d.GetMessage()}"));
        return $"{raw} | src={sourceDumpPath}";
    }

    /// <summary>
    /// Formats hard (non-retryable) diagnostics. Includes the CS code so the user can search for it.
    /// </summary>
    private static string FormatHardDiagnostics(IEnumerable<Diagnostic> errors) =>
        string.Join("; ", errors.Select(d => $"{d.Id}: {d.GetMessage()}"));

    /// <summary>
    /// Formats soft (retryable) diagnostics after retries are exhausted.
    /// Core returns generic compiler diagnostics; editor-specific friendly wording
    /// should be owned by LSP/UI.
    /// </summary>
    private static string FormatSoftDiagnostics(IEnumerable<Diagnostic> errors)
    {
        var messages = errors
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return string.Join("; ", messages);
    }

    // Matches both:
    //  - "The type or namespace name 'X' could not be found ..." (CS0246/CS0400)
    //  - "The type or namespace name 'X' does not exist in the namespace 'Y' ..." (CS0234)
    [GeneratedRegex(@"The type or namespace name '(.+?)' (could not be found|does not exist)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeOrNamespaceNamePattern();

    /// <summary>
    /// Extracts the simple type name from a CS0246 diagnostic message, e.g.
    /// "The type or namespace name 'CustomerRevenueDto' could not be found…" → "CustomerRevenueDto".
    /// Returns <see langword="null"/> if the pattern does not match.
    /// </summary>
    internal static string? TryExtractTypeNameFromCS0246(Diagnostic d)
    {
        var m = TypeOrNamespaceNamePattern().Match(d.GetMessage());
        return m.Success ? m.Groups[1].Value : null;
    }

    // "Argument N: cannot convert from 'actualType' to 'expectedType'"
    [GeneratedRegex(@"cannot convert from '.+?' to '(.+?)'", RegexOptions.CultureInvariant)]
    private static partial Regex Cs1503Pattern();

    /// <summary>
    /// Extracts the expected (target) type from a CS1503 diagnostic message, e.g.
    /// "Argument 3: cannot convert from 'object' to 'string?'" → "string?".
    /// Returns <see langword="null"/> if the pattern does not match.
    /// </summary>
    internal static string? TryExtractExpectedTypeFromCS1503(Diagnostic d)
    {
        var m = Cs1503Pattern().Match(d.GetMessage());
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string EnrichRuntimeFailureMessage(Exception exception)
    {
        var message = exception is TargetInvocationException
            ? exception.ToString()
            : exception.Message;

        if (exception.InnerException is not null)
        {
            var innerMessages = new List<string>();
            for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            {
                innerMessages.Add($"{inner.GetType().Name}: {inner.Message}");
            }

            if (innerMessages.Count > 0)
            {
                message += "\nInner exceptions: " + string.Join(" | ", innerMessages);
            }
        }

        if (message.Contains("does not have a type mapping assigned", StringComparison.OrdinalIgnoreCase))
        {
            message += "\n\nHint: A variable in your query has a type that EF Core cannot map to a SQL parameter type. " +
                       "This often happens with provider-specific value types (e.g. Pgvector.Vector for pgvector, " +
                       "NetTopologySuite.Geometries.Point for spatial). Ensure the variable is typed explicitly in " +
                       "the hovered expression, or assign it from a typed entity property.";
        }
        else if (exception is MissingMethodException ||
                 message.Contains("Method not found", StringComparison.OrdinalIgnoreCase))
        {
            message += "\n\nHint: A method expected by one EF Core assembly was not found in another. " +
                       "This is usually an intra-project version conflict — for example, the EF Core base package " +
                       "and a provider package (SQL Server, Pomelo, Npgsql) resolved to different major or minor " +
                       "versions in your project output. " +
                       "Check that all Microsoft.EntityFrameworkCore.* and provider package references in your " +
                       "project target the same version, and that no transitive dependency is pulling in a " +
                       "mismatched version.";
        }

        return message;
    }
}
