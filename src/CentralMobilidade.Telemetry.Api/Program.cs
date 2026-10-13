using CentralMobilidade.Telemetry.Application.Configuration;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTelemetryOptions(builder.Configuration);
var app = builder.Build();

app.MapGet("/", () => "Central Mobilidade Telemetry API");

app.Run();
