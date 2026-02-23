using Microsoft.EntityFrameworkCore;

namespace QueryLens.Core;

/// <summary>
/// Globals type injected into the Roslyn scripting sandbox.
/// User expressions write naturally against this: db.Orders.Where(o => o.UserId == 5)
/// </summary>
public sealed class QueryScriptGlobals
{
    public DbContext db { get; set; } = default!;
}
