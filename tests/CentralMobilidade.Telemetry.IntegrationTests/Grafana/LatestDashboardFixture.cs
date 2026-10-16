using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Endpoints.V1.Presentation;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Presentation;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Grafana;

// Lazy startup keeps unrelated protocol/missing-dashboard tests independent of rendering infrastructure.
public sealed class LatestDashboardFixture : IAsyncLifetime
{
    private readonly Lazy<Task> startup;
    private readonly INetwork network = new NetworkBuilder().Build();
    private IContainer? influx;
    private IContainer? renderer;
    private GrafanaServer? grafana;
    private MigratedPostgresFixture? postgres;
    private WebApplication? api;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "t072-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, JsonElement> settings = new();
    internal const string Company = "valid-unregistered-company";
    internal const string OtherCompany = "forbidden-company";
    internal DateTimeOffset Start { get; } = new(DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(-28), TimeSpan.Zero);
    public LatestDashboardFixture() => startup = new(StartAsync);
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task<JsonElement> GetSettingsAsync(string audience)
    {
        await startup.Value;
        return settings[audience];
    }

    private async Task StartAsync()
    {
        Directory.CreateDirectory(directory);
        await network.CreateAsync();
        influx = new ContainerBuilder("influxdb:3.11.6-core")
            .WithNetwork(network).WithNetworkAliases("influxdb").WithPortBinding(8181, true)
            .WithCommand("influxdb3", "serve", "--node-id", "t072", "--object-store", "memory", "--without-auth")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8181).ForPath("/health"))).Build();
        await influx.StartAsync();
        var influxUrl = $"http://{influx.Hostname}:{influx.GetMappedPublicPort(8181)}";
        using (var writer = new HttpClient { BaseAddress = new Uri(influxUrl) })
        {
            string Line(string company, string vehicle, int minute, int latitude) =>
                $"telemetria,empresa_id={company},veiculo_id={vehicle} latitude={latitude}.0,longitude=2.0,velocidade=42.0,rpm=1000i,ignicao=true {Start.AddMinutes(minute).ToUnixTimeMilliseconds()}";
            var lines = string.Join('\n', Line(Company, "vehicle-a", 10, 11), Line(Company, "vehicle-a", 0, 1), Line(Company, "vehicle-b", 5, 22), Line(OtherCompany, "vehicle-a", 20, 33));
            using var content = new StringContent(lines, Encoding.UTF8, "text/plain");
            using var response = await writer.PostAsync("/api/v3/write_lp?db=telemetry&precision=millisecond&accept_partial=false&no_sync=false", content);
            response.EnsureSuccessStatusCode();
        }
        var rendererToken = "t072-renderer-" + Guid.NewGuid().ToString("N");
        renderer = new ContainerBuilder("grafana/grafana-image-renderer:v4.0.17")
            .WithNetwork(network).WithNetworkAliases("renderer")
            .WithEnvironment("AUTH_TOKEN", rendererToken)
            .WithEnvironment("HTTP_PORT", "8081")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8081)).Build();
        await renderer.StartAsync();
        // Fresh isolated Grafana has only org 1. Validate the assigned org 2 before any credential is used.
        const int orgId = 2;
        var apiToken = OpaqueBearerTokens.Generate(BearerCredentialKind.ViewerPresentationService);
        var hash = BearerTokenHash.Create(apiToken);
        postgres = await MigratedPostgresFixture.StartAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:ViewerServices:0:CredentialId"] = "t072-viewer",
            ["Authentication:ViewerServices:0:ActorId"] = "t072-account",
            ["Authentication:ViewerServices:0:GrafanaOrganizationId"] = orgId.ToString(CultureInfo.InvariantCulture),
            ["Authentication:ViewerServices:0:Hash:LookupDigest"] = hash.LookupDigest,
            ["Authentication:ViewerServices:0:Hash:Salt"] = hash.Salt,
            ["Authentication:ViewerServices:0:Hash:Verifier"] = hash.Verifier,
            ["Authentication:ViewerServices:0:Hash:Iterations"] = hash.Iterations.ToString(CultureInfo.InvariantCulture),
            ["Authentication:Profiles:0:ActorId"] = "t072-account",
            ["Authentication:Profiles:0:Kind"] = "ViewerPresentationService",
            ["Authentication:Profiles:0:GrafanaOrganizationId"] = orgId.ToString(CultureInfo.InvariantCulture),
            ["Authentication:Profiles:0:CompanyIds:0"] = Company,
            ["Authentication:Profiles:0:Permissions:0"] = "telemetry.read",
            ["InfluxDb:Queryable:Url"] = influxUrl,
            ["InfluxDb:Queryable:Database"] = "telemetry",
            ["InfluxDb:Queryable:Token"] = "test-only-token"
        });
        builder.Services.AddTelemetryOptions(builder.Configuration);
        builder.Services.AddTelemetrySecurity(builder.Configuration);
        builder.Services.AddTelemetryControlPersistence(postgres.Connection);
        builder.Services.AddTelemetryPresentation(builder.Configuration);
        api = builder.Build();
        api.UseAuthentication();
        api.UseAuthorization();
        api.MapPresentationEndpoints();
        api.MapGet("/health/ready", () => Results.Json(new { status = "healthy" }));
        await api.StartAsync();
        var apiPort = new Uri(api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)apiPort);
        grafana = await GrafanaServer.StartAsync(Path.Combine(GrafanaProvisioningTests.Root, "deploy/grafana/provisioning"), network, rendererToken);
        using var createdOrg = await grafana.Client.PostAsJsonAsync("/api/orgs", new
        {
            name = "T072 restricted viewer"
        });
        createdOrg.EnsureSuccessStatusCode();
        using var orgJson = JsonDocument.Parse(await createdOrg.Content.ReadAsStringAsync());
        orgJson.RootElement.GetProperty("orgId").GetInt32().ShouldBe(orgId);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(grafana.Client.DefaultRequestHeaders.Authorization!.Parameter!));
        var bootstrapPassword = decoded[(decoded.IndexOf(':') + 1)..];
        var configuration = new
        {
            bootstrap = new
            {
                usernameEnv = "T072_BOOTSTRAP_USER",
                passwordEnv = "T072_BOOTSTRAP_PASSWORD"
            },
            readinessTimeoutSeconds = 30,
            organizations = new[] { new { name = "T072 restricted viewer", companyIds = new[] { Company }, members = Array.Empty<object>(),
                serviceAccount = new { name = "t072-reconciler", role = "Admin" },
                presentation = new { baseUrl = $"http://host.testcontainers.internal:{apiPort}", credentialEnv = "T072_PRESENTATION", permissions = new[] { "telemetry.read" } },
                datasourceUid = "viewer-api-latest", dashboardDirectory = Path.Combine(GrafanaProvisioningTests.Root, "deploy/grafana/dashboards/viewer") } }
        };
        var configurationPath = Path.Combine(directory, "configuration.json");
        await File.WriteAllTextAsync(configurationPath, JsonSerializer.Serialize(configuration));
        var processStart = new ProcessStartInfo(Environment.GetEnvironmentVariable("TELEMETRY_TEST_POWERSHELL") ?? "pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(GrafanaProvisioningTests.Root, "scripts/Provision-GrafanaOrganizations.ps1"), "-GrafanaUrl", grafana.Client.BaseAddress!.ToString(), "-ConfigurationPath", configurationPath, "-StateDirectory", Path.Combine(directory, "state") })
            processStart.ArgumentList.Add(argument);
        processStart.Environment["T072_BOOTSTRAP_USER"] = "bootstrap";
        processStart.Environment["T072_BOOTSTRAP_PASSWORD"] = bootstrapPassword;
        processStart.Environment["T072_PRESENTATION"] = apiToken;
        using (var process = Process.Start(processStart).ShouldNotBeNull())
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout + await stderr;
            output.ShouldNotContain(apiToken);
            output.ShouldNotContain(bootstrapPassword);
            process.ExitCode.ShouldBe(0, output);
        }
        foreach (var audience in new[] { "internal", "viewer" })
        {
            var id = audience == "internal" ? 1 : orgId;
            using var accountRequest = new HttpRequestMessage(HttpMethod.Post, "/api/serviceaccounts") { Content = JsonContent.Create(new { name = "t072-dashboard-reader", role = "Viewer" }) };
            accountRequest.Headers.Add("X-Grafana-Org-Id", id.ToString(CultureInfo.InvariantCulture));
            using var accountResponse = await grafana.Client.SendAsync(accountRequest);
            accountResponse.EnsureSuccessStatusCode();
            using var account = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync());
            var accountId = account.RootElement.GetProperty("id").GetInt32();
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/serviceaccounts/{accountId}/tokens") { Content = JsonContent.Create(new { name = "t072-reader", secondsToLive = 3600 }) };
            tokenRequest.Headers.Add("X-Grafana-Org-Id", id.ToString(CultureInfo.InvariantCulture));
            using var tokenResponse = await grafana.Client.SendAsync(tokenRequest);
            tokenResponse.EnsureSuccessStatusCode();
            using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
            settings[audience] = JsonSerializer.SerializeToElement(new
            {
                grafanaUrl = grafana.Client.BaseAddress.ToString(),
                orgId = id,
                serviceToken = token.RootElement.GetProperty("key").GetString(),
                variables = new Dictionary<string, string> { ["empresaId"] = Company, ["limit"] = "500", ["VIEWER_DATASOURCE_UID"] = "viewer-api-latest", ["from"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ["to"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) }
            });
        }
    }

    public async Task DisposeAsync()
    {
        if (api is not null)
            await api.DisposeAsync();
        if (grafana is not null)
            await grafana.DisposeAsync();
        if (renderer is not null)
            await renderer.DisposeAsync();
        if (influx is not null)
            await influx.DisposeAsync();
        if (postgres is not null)
            await postgres.DisposeAsync();
        await network.DisposeAsync();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
