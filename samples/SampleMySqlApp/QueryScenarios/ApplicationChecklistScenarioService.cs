using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SampleMySqlApp.Domain.Entities;
using SampleMySqlApp.Infrastructure.Persistence;

namespace SampleMySqlApp.QueryScenarios;

public sealed class ApplicationChecklistScenarioService(MySqlAppDbContext dbContext)
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
