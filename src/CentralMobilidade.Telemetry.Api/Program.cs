using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTelemetryOptions(builder.Configuration);
builder.Services.AddTelemetrySecurity(builder.Configuration);
var controlConnection = builder.Configuration.GetConnectionString("TelemetryControl");
if (!string.IsNullOrWhiteSpace(controlConnection))
    builder.Services.AddTelemetryControlPersistence(controlConnection);
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "Central Mobilidade Telemetry API");

app.Run();
