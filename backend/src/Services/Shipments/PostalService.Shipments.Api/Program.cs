using PostalService.BuildingBlocks.Infrastructure.Persistence;
using PostalService.BuildingBlocks.Infrastructure.Web;
using PostalService.Shipments.Application;
using PostalService.Shipments.Infrastructure;
using PostalService.Shipments.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("shipments-api");
builder.Services.AddPostalAuthentication(builder.Configuration);
builder.Services.AddShipmentsApplication();
builder.Services.AddShipmentsInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks().AddDatabaseHealthCheck<ShipmentsDbContext>();

var app = builder.Build();

app.UseServiceDefaults();
await app.InitializeDatabaseAsync<ShipmentsDbContext>();
await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
