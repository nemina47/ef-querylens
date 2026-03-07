namespace SampleApp.QueryScenarios;

public sealed class ApplicationChecklistResponse
{
    public Guid ApplicationId { get; set; }
    public List<string> ChangeTypes { get; set; } = [];
}
