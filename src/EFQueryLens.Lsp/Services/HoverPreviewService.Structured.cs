using EFQueryLens.Core;
using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Lsp.Services;

internal sealed partial class HoverPreviewService
{
    public async Task<QueryLensStructuredHoverResult> BuildStructuredAsync(
        string filePath,
        string sourceText,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        Action<string> log = message => LogDebug($"structured {message}");
        var canonical = await BuildCanonicalAsync(
            filePath,
            sourceText,
            line,
            character,
            cancellationToken,
            log);

        return FormatStructured(canonical, filePath);
    }

    private QueryLensStructuredHoverResult FormatStructured(
        HoverCanonicalComputationResult canonical,
        string filePath)
    {
        static QueryLensStructuredHoverResult Fail(
            string msg,
            QueryTranslationStatus status = QueryTranslationStatus.Ready,
            double avgTranslationMs = 0,
            double lastTranslationMs = 0) =>
            new(false, msg, [], 0, null, null, null, null, null, 0, [], null, null, status, msg, avgTranslationMs, lastTranslationMs);

        if (canonical.Status is not QueryTranslationStatus.Ready && canonical.Success)
        {
            return new QueryLensStructuredHoverResult(
                Success: false,
                ErrorMessage: canonical.Message,
                Statements: [],
                CommandCount: 0,
                SourceExpression: canonical.SourceExpression,
                ExecutedExpression: null,
                DbContextType: null,
                ProviderName: null,
                SourceFile: filePath,
                SourceLine: canonical.SourceLine,
                Warnings: [],
                EnrichedSql: null,
                Mode: "queued",
                Status: canonical.Status,
                StatusMessage: canonical.Message,
                AvgTranslationMs: canonical.AvgTranslationMs,
                LastTranslationMs: canonical.LastTranslationMs);
        }

        if (!canonical.Success)
        {
            return Fail(canonical.Message, canonical.Status, canonical.AvgTranslationMs, canonical.LastTranslationMs);
        }

        var providerName = canonical.Metadata?.ProviderName;
        var statements = BuildFormattedStatements(canonical.Commands, providerName);
        var warnings = BuildWarningLines(canonical.Warnings);

        var fullSql = statements.Count == 0
            ? null
            : BuildStatementsSqlBlock(statements);
        var enrichedSql = BuildStructuredEnrichedSql(
            fullSql,
            sourceFile: filePath,
            sourceLine: canonical.SourceLine,
            sourceExpression: canonical.SourceExpression,
            executedExpression: canonical.ExecutedExpression,
            efCoreVersion: canonical.Metadata?.EfCoreVersion,
            dbContextType: canonical.Metadata?.DbContextType,
            providerName: canonical.Metadata?.ProviderName,
            warnings: canonical.Warnings,
            hasClientEvaluation: canonical.Metadata?.HasClientEvaluation ?? false,
            parameters: canonical.Commands.Count > 0
                ? canonical.Commands.SelectMany(c => c.Parameters)
                    .GroupBy(p => p.Name, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .ToList()
                : []);

        return new QueryLensStructuredHoverResult(
            Success: true,
            ErrorMessage: null,
            Statements: statements,
            CommandCount: canonical.Commands.Count,
            SourceExpression: canonical.SourceExpression,
            ExecutedExpression: canonical.ExecutedExpression,
            DbContextType: canonical.Metadata?.DbContextType,
            ProviderName: canonical.Metadata?.ProviderName,
            SourceFile: filePath,
            SourceLine: canonical.SourceLine,
            Warnings: warnings,
            EnrichedSql: enrichedSql,
            Mode: "direct",
            Status: QueryTranslationStatus.Ready,
            StatusMessage: null,
            AvgTranslationMs: canonical.AvgTranslationMs,
            LastTranslationMs: canonical.LastTranslationMs);
    }
}
