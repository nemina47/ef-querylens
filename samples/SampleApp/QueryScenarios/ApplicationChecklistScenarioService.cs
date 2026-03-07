using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SampleApp.Entities;

namespace SampleApp.QueryScenarios;

public sealed class ApplicationChecklistScenarioService(AppDbContext dbContext)
{
    public Task<TResult?> GetChecklistByApplicationIdAsync<TResult>(
        Guid applicationId,
        Expression<Func<ApplicationChecklist, TResult>> expression,
        CancellationToken ct)
    {
        return dbContext.ApplicationChecklists
            .AsNoTracking()
            .Where(w => !w.IsDeleted && w.IsLatest)
            .Where(w => w.ApplicationId == applicationId)
            .Select(expression)
            .SingleOrDefaultAsync(ct);
    }

    public Task<List<string>> GetChecklistChangeTypesAsync(
        Guid applicationId,
        CancellationToken ct)
    {
        return dbContext.ApplicationChecklists
            .AsNoTracking()
            .Where(w => !w.IsDeleted && w.IsLatest)
            .Where(w => w.ApplicationId == applicationId)
            .SelectMany(x => x.ChecklistChangeTypes)
            .Where(w => !w.IsDeleted)
            .Select(s => s.ChangeType)
            .ToListAsync(ct);
    }
}
