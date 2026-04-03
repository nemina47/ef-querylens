using Microsoft.EntityFrameworkCore;
using SampleDbContextFactoryApp.Domain;
using SampleDbContextFactoryApp.Infrastructure.Persistence;

namespace SampleDbContextFactoryApp.Application;

public sealed class DataService
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

    public DataService(IDbContextFactory<ApplicationDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<List<Rationale>> GetAllRationalesAsync(CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.Rationales
            .AsNoTracking()
            .OrderBy(x => x.Title)
            .ToListAsync(ct);
    }

    public async Task<List<Rationale>> SearchByTitleAsync(string term, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.Rationales
            .AsNoTracking()
            .Where(x => x.Title.ToLower().Contains(term.ToLower()))
            .ToListAsync(ct);
    }
}
