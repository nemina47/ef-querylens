namespace EFQueryLens.Core;

public sealed record QueryWarning
{
    public required WarningSeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Suggestion { get; init; }
}
