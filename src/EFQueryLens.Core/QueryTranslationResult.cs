namespace EFQueryLens.Core;

public record QueryTranslationResult
{
    public bool Success { get; init; }
    public string? Sql { get; init; }
    public IReadOnlyList<QuerySqlCommand> Commands { get; init; } = [];
    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];
    public IReadOnlyList<QueryWarning> Warnings { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public TranslationMetadata Metadata { get; init; } = default!;
}
