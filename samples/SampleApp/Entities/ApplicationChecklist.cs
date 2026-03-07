namespace SampleApp.Entities;

public class ApplicationChecklist
{
    public int Id { get; set; }
    public Guid ApplicationId { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsLatest { get; set; }

    public ICollection<ApplicationChecklistChangeType> ChecklistChangeTypes { get; set; } = [];
}
