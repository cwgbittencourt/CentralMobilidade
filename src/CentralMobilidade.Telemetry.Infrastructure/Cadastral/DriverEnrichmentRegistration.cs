using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using CentralMobilidade.Telemetry.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CentralMobilidade.Telemetry.Infrastructure.Cadastral;

public static class DriverEnrichmentRegistration
{
    public static IServiceCollection AddOptionalDriverEnrichment(this IServiceCollection services)
    {
        services.AddHttpClient("DriverEnrichment", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            }).RemoveAllLoggers();
        services.TryAddSingleton<IDriverResolver>(provider => new ConfiguredCadastralAdapter(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("DriverEnrichment"),
            provider.GetRequiredService<IConfiguration>(), provider.GetService<IClock>() ?? new Clock()));
        return services;
    }
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
