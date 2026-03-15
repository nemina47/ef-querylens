namespace EFQueryLens.Core;

public sealed record QueuedTranslationResult
{
    public QueryTranslationStatus Status { get; init; }

    public string? JobId { get; init; }

    public double AverageTranslationMs { get; init; }

    public QueryTranslationResult? Result { get; init; }
}