using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence;

public sealed class TelemetryControlDesignTimeFactory : IDesignTimeDbContextFactory<TelemetryControlDbContext>
{
    public TelemetryControlDbContext CreateDbContext(string[] args)
    {
        // Scaffolding/script generation is offline. Updates must receive a secret-sourced connection.
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__TelemetryControl")
            ?? "Host=localhost;Database=telemetry_control_design_only;Username=design_only";
        return new(new DbContextOptionsBuilder<TelemetryControlDbContext>().UseNpgsql(connection).Options);
    }
}
