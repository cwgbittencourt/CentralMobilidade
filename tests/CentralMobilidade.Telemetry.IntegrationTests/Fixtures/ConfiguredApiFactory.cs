using System.Collections.Generic;
using System.Linq;
using CentralMobilidade.Telemetry.Api.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CentralMobilidade.Telemetry.IntegrationTests.Fixtures;

// Minimal API startup reads configuration before Build. Supply it through host configuration,
// which WebApplicationFactory passes to the entry point before its service-registration code runs.
internal class ConfiguredApiFactory(IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<ProducerBearerHandler>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(settings.Where(pair => !pair.Key.StartsWith("Authentication:", System.StringComparison.Ordinal)).Concat(
            new[] { new KeyValuePair<string, string?>(HostDefaults.EnvironmentKey, "Testing") })));
        return base.CreateHost(builder);
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
}
