using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DotNet.Testcontainers.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Grafana;

public sealed class GrafanaReconciliationRuntimeTests
{
    [Fact]
    public async Task Internal_YAML_provisions_only_readonly_internal_sources_before_business_dashboards_exist()
    {
        await using var grafana = await GrafanaServer.StartAsync(Path.Combine(GrafanaProvisioningTests.Root, "deploy/grafana/provisioning"));
        using var organizations = await grafana.JsonAsync("/api/orgs");
        organizations.RootElement.GetArrayLength().ShouldBe(1);
        using var sources = await grafana.JsonAsync("/api/datasources");
        sources.RootElement.EnumerateArray().Select(source => source.GetProperty("type").GetString()).Order().ShouldBe(new[] { "influxdb", "loki", "prometheus" });
        sources.RootElement.EnumerateArray().ShouldAllBe(source => source.GetProperty("readOnly").GetBoolean());
        var influx = sources.RootElement.EnumerateArray().Single(source => source.GetProperty("type").GetString() == "influxdb");
        influx.GetProperty("jsonData").GetProperty("version").GetString().ShouldBe("SQL");
        influx.GetProperty("jsonData").GetProperty("dbName").GetString().ShouldBe("telemetry");
    }

    [Fact]
    public async Task Production_script_converges_and_rotates_on_real_Grafana_with_authenticated_Infinity_backend_health()
    {
        var presentationSecret = "t071-presentation-" + Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        var calls = 0;
        api.MapGet("/health/ready", (HttpContext context) =>
        {
            if (context.Request.Headers.Authorization.ToString() != "Bearer " + presentationSecret)
                return Results.Unauthorized();
            calls++;
            return Results.Json(new
            {
                status = "healthy"
            });
        });
        await api.StartAsync();
        var address = api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var port = new Uri(address).Port;
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)port);
        await using var grafana = await GrafanaServer.StartAsync();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(grafana.Client.DefaultRequestHeaders.Authorization!.Parameter!));
        var password = decoded[(decoded.IndexOf(':') + 1)..];
        var directory = Path.Combine(Path.GetTempPath(), "t071-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var dashboardDirectory = Path.Combine(directory, "dashboards");
            Directory.CreateDirectory(dashboardDirectory);
            await File.WriteAllTextAsync(Path.Combine(dashboardDirectory, "fixture.json"), """{"uid":"t071-runtime","title":"Reconciliation fixture only","schemaVersion":41,"panels":[]}""");
            var configuration = new
            {
                bootstrap = new
                {
                    usernameEnv = "T071_BOOTSTRAP_USER",
                    passwordEnv = "T071_BOOTSTRAP_PASSWORD"
                },
                readinessTimeoutSeconds = 30,
                organizations = new[] { new {
                    name = "T071 isolated viewer", companyIds = new[] { "company-a" }, members = Array.Empty<object>(),
                    serviceAccount = new { name = "t071-dedicated", role = "Admin" },
                    presentation = new { baseUrl = $"http://host.testcontainers.internal:{port}", credentialEnv = "T071_PRESENTATION", permissions = new[] { "telemetry.read" } },
                    datasourceUid = "t071-viewer", dashboardDirectory
                } }
            };
            var configurationPath = Path.Combine(directory, "configuration.json");
            await File.WriteAllTextAsync(configurationPath, JsonSerializer.Serialize(configuration));
            async Task RunAsync()
            {
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("TELEMETRY_TEST_POWERSHELL") ?? "pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(GrafanaProvisioningTests.Root, "scripts/Provision-GrafanaOrganizations.ps1"), "-GrafanaUrl", grafana.Client.BaseAddress!.ToString(), "-ConfigurationPath", configurationPath, "-StateDirectory", Path.Combine(directory, "state") })
                    start.ArgumentList.Add(argument);
                start.Environment["T071_BOOTSTRAP_USER"] = "bootstrap";
                start.Environment["T071_BOOTSTRAP_PASSWORD"] = password;
                start.Environment["T071_PRESENTATION"] = presentationSecret;
                using var process = Process.Start(start).ShouldNotBeNull();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                }
                catch { process.Kill(entireProcessTree: true); throw; }
                var output = await stdout + await stderr;
                output.ShouldNotContain(password);
                output.ShouldNotContain(presentationSecret);
                process.ExitCode.ShouldBe(0, output);
            }
            await RunAsync();
            using var orgs = await grafana.JsonAsync("/api/orgs");
            orgs.RootElement.GetArrayLength().ShouldBe(2);
            var orgId = orgs.RootElement.EnumerateArray().Single(org => org.GetProperty("name").GetString() == "T071 isolated viewer").GetProperty("id").GetInt32();
            using var orgClient = new HttpClient { BaseAddress = grafana.Client.BaseAddress };
            orgClient.DefaultRequestHeaders.Authorization = grafana.Client.DefaultRequestHeaders.Authorization;
            orgClient.DefaultRequestHeaders.Add("X-Grafana-Org-Id", orgId.ToString(CultureInfo.InvariantCulture));
            using var accounts = JsonDocument.Parse(await orgClient.GetStringAsync("/api/serviceaccounts/search"));
            var accountId = accounts.RootElement.GetProperty("serviceAccounts").EnumerateArray().Single().GetProperty("id").GetInt32();
            var tokenPath = $"/api/serviceaccounts/{accountId}/tokens";
            using var tokensBefore = JsonDocument.Parse(await orgClient.GetStringAsync(tokenPath));
            var firstId = tokensBefore.RootElement.EnumerateArray().Single().GetProperty("id").GetInt32();
            await RunAsync();
            using var tokensAfter = JsonDocument.Parse(await orgClient.GetStringAsync(tokenPath));
            tokensAfter.RootElement.EnumerateArray().Single().GetProperty("id").GetInt32().ShouldNotBe(firstId);
            var exported = await orgClient.GetStringAsync("/api/datasources/uid/t071-viewer");
            exported.ShouldNotContain(presentationSecret);
            exported.ShouldNotContain(password);
            using var source = JsonDocument.Parse(exported);
            source.RootElement.GetProperty("type").GetString().ShouldBe(GrafanaProvisioningTests.Plugin);
            source.RootElement.GetProperty("secureJsonFields").GetProperty("bearerToken").GetBoolean().ShouldBeTrue();
            using var dashboard = await orgClient.GetAsync("/api/dashboards/uid/t071-runtime");
            dashboard.StatusCode.ShouldBe(HttpStatusCode.OK);
            calls.ShouldBeGreaterThanOrEqualTo(2);
            Directory.GetFiles(Path.Combine(directory, "state")).Select(Path.GetFileName).Order().ShouldBe(new[] { "managed.json", "reconcile.lock" });
            var metadata = await File.ReadAllTextAsync(Path.Combine(directory, "state/managed.json"));
            metadata.ShouldNotContain(password);
            metadata.ShouldNotContain(presentationSecret);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
