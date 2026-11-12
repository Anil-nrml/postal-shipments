using PostalService.BuildingBlocks.Domain;
using PostalService.BuildingBlocks.Infrastructure.Persistence;
using PostalService.BuildingBlocks.Infrastructure.Web;
using PostalService.PostOffices.Application;
using PostalService.PostOffices.Domain;
using PostalService.PostOffices.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("postoffices-api");
builder.Services.AddPostalAuthentication(builder.Configuration);
builder.Services.AddPostOfficesApplication();
builder.Services.AddPostOfficesInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks().AddDatabaseHealthCheck<PostOfficesDbContext>();

var app = builder.Build();

app.UseServiceDefaults();
await app.InitializeDatabaseAsync<PostOfficesDbContext>(async (db, sp) =>
{
    // Demo data for local development only. Goes through the aggregate so events reach the outbox.
    if (!app.Environment.IsDevelopment() || db.PostOffices.Any()) return;
    var now = DateTime.UtcNow;
    db.PostOffices.AddRange(
        PostOffice.Create(ZipCode.Create("500001"), "Hyderabad GPO", Address.Create("Abids Road", "Hyderabad", "Telangana", "India"), "+91 40 2346 3000", now),
        PostOffice.Create(ZipCode.Create("500081"), "Madhapur SO", Address.Create("Hitech City Road", "Hyderabad", "Telangana", "India"), "+91 40 2311 1100", now),
        PostOffice.Create(ZipCode.Create("560001"), "Bengaluru GPO", Address.Create("Raj Bhavan Road", "Bengaluru", "Karnataka", "India"), "+91 80 2286 6000", now),
        PostOffice.Create(ZipCode.Create("400001"), "Mumbai GPO", Address.Create("Walchand Hirachand Marg", "Mumbai", "Maharashtra", "India"), "+91 22 2262 0693", now),
        PostOffice.Create(ZipCode.Create("110001"), "New Delhi GPO", Address.Create("Ashoka Road", "New Delhi", "Delhi", "India"), "+91 11 2336 4111", now));
    await db.SaveChangesAsync();
});
await app.RunAsync();

public partial class Program;
