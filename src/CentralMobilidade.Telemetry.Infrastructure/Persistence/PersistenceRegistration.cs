using System;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence;

public static class PersistenceRegistration
{
    public static IServiceCollection AddTelemetryControlPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<TelemetryControlDbContext>(options => options.UseNpgsql(connectionString));
        services.TryAddSingleton<IAuditWriter, PostgresAuditWriter>();
        services.TryAddSingleton<IBatchStateStore, PostgresBatchStateStore>();
        return services;
    }
}
