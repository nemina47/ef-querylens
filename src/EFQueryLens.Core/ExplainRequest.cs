namespace EFQueryLens.Core;

public sealed record ExplainRequest : TranslationRequest
{
    public required string ConnectionString { get; init; }

    /// <summary>
    /// When true, uses EXPLAIN ANALYZE (MySQL 8.0.18+ / Aurora 3.x).
    /// Falls back to EXPLAIN FORMAT=JSON on older server versions.
    /// </summary>
    public bool UseAnalyze { get; init; } = true;
}
