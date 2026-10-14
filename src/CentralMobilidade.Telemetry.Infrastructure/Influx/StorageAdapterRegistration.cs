using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.Cadastral;
using Microsoft.Extensions.DependencyInjection;

namespace CentralMobilidade.Telemetry.Infrastructure.Influx;

public static class StorageAdapterRegistration
{
    public static IServiceCollection AddTelemetryStorageAdapters(this IServiceCollection services)
    {
        services.AddHttpClient<ITelemetryWriter, InfluxV3TelemetryWriter>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CreateHandler).RedactLoggedHeaders(_ => true);
        services.AddHttpClient<ICompanyVisibilityResolver, ConfiguredCadastralAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CreateHandler).RedactLoggedHeaders(_ => true);
        return services;
    }
    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };
}
