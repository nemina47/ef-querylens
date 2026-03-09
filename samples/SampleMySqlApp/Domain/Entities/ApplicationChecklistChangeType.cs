namespace SampleMySqlApp.Domain.Entities;

public sealed class ApplicationChecklistChangeType
{
    public int Id { get; set; }
    public int ApplicationChecklistId { get; set; }
    public bool IsDeleted { get; set; }
    public string ChangeType { get; set; } = string.Empty;

    public ApplicationChecklist ApplicationChecklist { get; set; } = null!;
}
