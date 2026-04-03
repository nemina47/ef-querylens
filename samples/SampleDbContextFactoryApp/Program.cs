using Microsoft.EntityFrameworkCore;
using SampleDbContextFactoryApp.Application;
using SampleDbContextFactoryApp.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite("Data Source=sample-factory.db"));
builder.Services.AddScoped<DataService>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { Message = "SampleDbContextFactoryApp is running." }));

app.MapGet("/api/rationales", async (DataService service, CancellationToken ct) =>
{
    var results = await service.GetAllRationalesAsync(ct);
    return Results.Ok(results);
});

app.MapGet("/api/rationales/search", async (string term, DataService service, CancellationToken ct) =>
{
    var results = await service.SearchByTitleAsync(term, ct);
    return Results.Ok(results);
});

app.Run();
