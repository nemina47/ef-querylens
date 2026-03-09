namespace SampleMySqlApp.Domain.Entities;

public sealed class ApplicationChecklist
{
    public int Id { get; set; }
    public Guid ApplicationId { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsLatest { get; set; }

    public ICollection<ApplicationChecklistChangeType> ChecklistChangeTypes { get; set; } = new List<ApplicationChecklistChangeType>();
}
