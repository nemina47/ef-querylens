namespace EFQueryLens.Core.Contracts;

public record QueryTranslationResult
{
    public bool Success { get; init; }
    public string? Sql { get; init; }
    public IReadOnlyList<QuerySqlCommand> Commands { get; init; } = [];
    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];
    public IReadOnlyList<QueryWarning> Warnings { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public TranslationMetadata Metadata { get; init; } = null!;

    /// <summary>
    /// The expression actually evaluated after EFQueryLens rewrites
    /// (Find normalization, context-hop fix, pattern rewrite, etc).
    /// Null when identical to the source expression passed in.
    /// </summary>
    public string? ExecutedExpression { get; init; }
}
