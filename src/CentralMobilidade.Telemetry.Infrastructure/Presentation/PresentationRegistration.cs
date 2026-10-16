using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Presentation;
using CentralMobilidade.Telemetry.Infrastructure.Cadastral;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CentralMobilidade.Telemetry.Infrastructure.Presentation;

public static class PresentationRegistration
{
    public static IServiceCollection AddTelemetryPresentation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTelemetryOptions(configuration);
        services.AddOptionalDriverEnrichment();
        services.AddHttpClient("TelemetryPresentation", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            }).RemoveAllLoggers();
        services.TryAddScoped<ITelemetryPresentationReader>(provider => new InfluxPresentationReader(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("TelemetryPresentation"), configuration));
        services.TryAddScoped<IIngestionHealthReader>(provider => new PostgresIngestionHealthReader(provider.GetService<IDbContextFactory<TelemetryControlDbContext>>()));
        services.TryAddSingleton<IClock, Clock>();
        services.TryAddScoped<PresentationAccess>();
        services.TryAddScoped<LatestPositionHandler>();
        services.TryAddScoped<VehicleTripHandler>();
        services.TryAddScoped<IngestionHealthHandler>();
        services.TryAddScoped<DriverPresentationHandler>();
        return services;
    }
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
