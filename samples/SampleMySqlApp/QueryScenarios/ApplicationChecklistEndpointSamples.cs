namespace SampleMySqlApp.QueryScenarios;

public static class ApplicationChecklistEndpointSamples
{
    public static Task<ApplicationChecklistResponse?> GetChecklistPreviewAsync(
        ApplicationChecklistScenarioService service,
        Guid applicationId,
        CancellationToken ct)
    {
        return service.GetChecklistByApplicationIdAsync(
            applicationId,
            checklist => new ApplicationChecklistResponse
            {
                ApplicationId = checklist.ApplicationId,
                ChangeTypes = checklist.ChecklistChangeTypes
                    .Where(t => !t.IsDeleted)
                    .Select(t => t.ChangeType)
                    .ToList()
            },
            ct);
    }

    public static Task<List<string>> GetChecklistChangeTypesAsync(
        ApplicationChecklistScenarioService service,
        Guid applicationId,
        CancellationToken ct)
    {
        return service.GetChecklistChangeTypesAsync(applicationId, ct);
    }
}
