using System;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.Auditing;
using CentralMobilidade.Telemetry.Infrastructure.Idempotency;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
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
        services.TryAddSingleton<BatchMetrics>();
        services.TryAddSingleton<IEventIdentityGuard, SelectedEventIdentityGuard>();
        return services;
    }
}
