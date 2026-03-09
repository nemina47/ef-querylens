namespace EFQueryLens.Core;

public sealed record ExplainResult : QueryTranslationResult
{
    public ExplainNode? Plan { get; init; }

    /// <summary>
    /// false means the plan contains estimates only (EXPLAIN without ANALYZE).
    /// </summary>
    public bool IsActualExecution { get; init; }

    public string? ServerVersion { get; init; }
}
