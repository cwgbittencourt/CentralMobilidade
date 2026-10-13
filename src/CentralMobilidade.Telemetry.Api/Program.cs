using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Configuration;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTelemetryOptions(builder.Configuration);
builder.Services.AddTelemetrySecurity(builder.Configuration);
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "Central Mobilidade Telemetry API");

app.Run();
