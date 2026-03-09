namespace EFQueryLens.Core;

/// <summary>
/// Provider-agnostic normalized plan node.
/// MySQL, Postgres, and SQL Server all parse to this same structure.
/// </summary>
public sealed record ExplainNode
{
    public required string OperationType { get; init; }
    public string? TableName { get; init; }

    /// <summary>null means full scan.</summary>
    public string? IndexUsed { get; init; }

    public double EstimatedCost { get; init; }
    public long EstimatedRows { get; init; }

    /// <summary>null when ANALYZE was not used.</summary>
    public long? ActualRows { get; init; }

    public int? LoopCount { get; init; }
    public IReadOnlyList<ExplainNode> Children { get; init; } = [];
    public IReadOnlyList<QueryWarning> Warnings { get; init; } = [];

    /// <summary>
    /// Ratio of actual to estimated rows.
    /// Useful for visualizer color-coding. null when ActualRows is unavailable.
    /// </summary>
    public double? RowEstimateAccuracy =>
        ActualRows.HasValue && EstimatedRows > 0
            ? (double)ActualRows.Value / EstimatedRows
            : null;
}
