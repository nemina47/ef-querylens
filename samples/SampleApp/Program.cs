using System.Linq;
using Microsoft.EntityFrameworkCore;
using SampleApp;

// SampleApp is used as the dogfood target for QueryLens integration tests.
Console.WriteLine("SampleApp is a test fixture for EFQueryLens. See tests/EFQueryLens.Integration.Tests.");

using var db = new AppDbContext(null!);
var orders = db.Orders.Where(o => o.Id == 1).ToList();
