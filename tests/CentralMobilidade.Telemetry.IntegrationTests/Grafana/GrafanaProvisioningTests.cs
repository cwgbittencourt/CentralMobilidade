using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
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

public sealed class GrafanaProvisioningTests
{
    internal const string Plugin = "yesoreyeram-infinity-datasource";
    internal static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CentralMobilidade.slnx")))
                directory = directory.Parent;
            return directory.ShouldNotBeNull().FullName;
        }
    }

    [Fact]
    public void Docker_build_pins_Grafana_and_signed_Infinity_outside_the_persistent_data_path()
    {
        var dockerfile = File.ReadAllText(Path.Combine(Root, "deploy/grafana/Dockerfile"));
        dockerfile.ShouldContain("FROM grafana/grafana:12.2.5");
        dockerfile.ShouldContain("plugins install " + Plugin + " 4.1.1");
        dockerfile.ShouldContain("GF_PATHS_PLUGINS=/opt/grafana/plugins");
        dockerfile.ShouldContain("MANIFEST.txt");
        dockerfile.ShouldNotContain("allow_loading_unsigned_plugins");
        var compose = File.ReadAllText(Path.Combine(Root, "deploy/compose.yaml"));
        compose.ShouldContain("centralmobilidade-grafana:12.2.5-infinity-4.1.1");
        compose.ShouldContain("GF_PLUGINS_PREINSTALL_DISABLED: \"true\"");
        compose.ShouldContain("GF_AUTH_ANONYMOUS_ENABLED: \"false\"");
    }

    [Fact]
    [Trait("PendingImplementation", "T071-T074")]
    public async Task Internal_YAML_is_loaded_by_real_Grafana_and_creates_only_the_internal_organization()
    {
        var provisioning = Path.Combine(Root, "deploy/grafana/provisioning");
        Directory.Exists(provisioning).ShouldBeTrue("T071 must provide internal-only YAML provisioning.");
        Directory.GetFiles(provisioning, "*.y*ml", SearchOption.AllDirectories).ShouldNotBeEmpty();
        await using var grafana = await GrafanaServer.StartAsync(provisioning);
        using var organizations = await grafana.JsonAsync("/api/orgs");
        organizations.RootElement.GetArrayLength().ShouldBe(1);
        using var datasources = await grafana.JsonAsync("/api/datasources");
        var types = datasources.RootElement.EnumerateArray().Select(d => d.GetProperty("type").GetString()).ToArray();
        types.ShouldContain("influxdb");
        types.ShouldContain("prometheus");
        types.ShouldContain("loki");
        types.ShouldNotContain(Plugin);
        foreach (var datasource in datasources.RootElement.EnumerateArray())
            datasource.GetProperty("readOnly").GetBoolean().ShouldBeTrue();
        using var dashboards = await grafana.JsonAsync("/api/search?type=dash-db");
        dashboards.RootElement.GetArrayLength().ShouldBeGreaterThanOrEqualTo(3);
        var internalDirectory = Path.Combine(Root, "deploy/grafana/dashboards/internal");
        Directory.Exists(internalDirectory).ShouldBeTrue("T072-T074 must supply the internal dashboards referenced by YAML.");
        var expectedUids = Directory.GetFiles(internalDirectory, "*.json").Select(file =>
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            return json.RootElement.GetProperty("uid").GetString();
        }).ToArray();
        dashboards.RootElement.EnumerateArray().Select(d => d.GetProperty("uid").GetString()).Order().ShouldBe(expectedUids.Order());
    }

    [Fact]
    [Trait("PendingImplementation", "T071")]
    public async Task Reconciler_converges_twice_with_Basic_bootstrap_org_local_tokens_and_separate_presentation_secrets()
    {
        RequireScript();
        await using var api = await ProvisioningApi.StartAsync();
        await api.RunAsync(success: true);
        var first = api.Resources;
        await api.RunAsync(success: true);
        api.Resources.ShouldBe(first);
        api.Organizations.Count.ShouldBe(2);
        api.Datasources.Count.ShouldBe(2);
        api.Dashboards.Count.ShouldBe(2);
        api.TokenOrganizations.Count.ShouldBe(2, "Repeated reconciliation must reuse or revoke replaced organization tokens.");
        foreach (var request in api.Requests)
            foreach (var presentation in api.Presentation)
                request.Authorization.ShouldNotBe("Bearer " + presentation);
        api.Requests.ShouldContain(r => r.Path.StartsWith("/api/orgs", StringComparison.Ordinal) && r.Authorization == api.Basic);
        foreach (var request in api.Requests.Where(r => r.Path.StartsWith("/api/orgs", StringComparison.Ordinal)))
            request.Authorization.ShouldBe(api.Basic);
        foreach (var request in api.Requests.Where(r => r.Path.StartsWith("/api/datasources", StringComparison.Ordinal) || r.Path == "/api/dashboards/db"))
        {
            request.Authorization.ShouldStartWith("Bearer gsa-test-");
            api.TokenOrganizations[request.Authorization[7..]].ShouldBe(request.Organization);
        }
        foreach (var (org, datasource) in api.Datasources)
        {
            datasource.GetProperty("uid").GetString().ShouldBe("viewer-api-" + (org - 2));
            datasource.GetProperty("type").GetString().ShouldBe(Plugin);
            var secret = datasource.GetProperty("secureJsonData").GetProperty("bearerToken").GetString();
            secret.ShouldBe(api.Presentation[org - 2]);
            var serialized = datasource.GetRawText();
            serialized.ShouldNotContain(api.Password);
            foreach (var token in api.TokenOrganizations.Keys)
                serialized.ShouldNotContain(token);
            datasource.GetProperty("jsonData").GetProperty("allowedHosts").GetArrayLength().ShouldBeGreaterThan(0);
        }
        api.Requests.Where(r => r.Path == "/api/serviceaccounts" && r.Method == "POST").ShouldAllBe(r => r.Organization > 1 && !r.Body.Contains("GrafanaAdmin", StringComparison.Ordinal));
        api.Requests.ShouldContain(r => r.Path.EndsWith("/users", StringComparison.Ordinal) && r.Method == "POST");
        api.HealthyBeforeMutation.ShouldBeTrue();
    }

    [Theory]
    [Trait("PendingImplementation", "T071")]
    [InlineData("grafana-version")]
    [InlineData("plugin-version")]
    [InlineData("plugin-missing")]
    [InlineData("plugin-unsigned")]
    [InlineData("unhealthy")]
    public async Task Reconciler_stops_before_mutation_on_wrong_versions_signature_or_unhealthy_service(string fault)
    {
        RequireScript();
        await using var api = await ProvisioningApi.StartAsync(fault);
        await api.RunAsync(success: false);
        api.Requests.Where(r => r.Method is "POST" or "PUT" or "PATCH" or "DELETE").ShouldBeEmpty();
    }

    [Theory]
    [Trait("PendingImplementation", "T071")]
    [InlineData("bootstrap")]
    [InlineData("other-viewer")]
    public async Task Reconciler_rejects_reused_credentials_without_disclosing_them(string collision)
    {
        RequireScript();
        await using var api = await ProvisioningApi.StartAsync(collision);
        await api.RunAsync(success: false);
        api.Datasources.ShouldBeEmpty();
    }

    [Theory]
    [Trait("PendingImplementation", "T071")]
    [InlineData("service-token-as-bootstrap")]
    [InlineData("organization-mismatch")]
    public async Task Reconciler_cannot_expand_scope_or_use_a_service_token_for_admin_organization_calls(string fault)
    {
        RequireScript();
        await using var api = await ProvisioningApi.StartAsync(fault);
        await api.RunAsync(success: false);
        api.Datasources.ShouldBeEmpty();
    }

    [Theory]
    [Trait("PendingImplementation", "T072-T074")]
    [InlineData("internal", "latest-vehicle-position")]
    [InlineData("internal", "vehicle-trip")]
    [InlineData("internal", "company-ingestion-health")]
    [InlineData("viewer", "latest-vehicle-position")]
    [InlineData("viewer", "vehicle-trip")]
    [InlineData("viewer", "company-ingestion-health")]
    [InlineData("viewer", "driver-presentation")]
    public async Task Every_production_panel_has_real_query_frames_and_renders_to_PNG(string audience, string dashboard)
    {
        var file = Path.Combine(Root, "deploy/grafana/dashboards", audience, dashboard + ".json");
        File.Exists(file).ShouldBeTrue("T072-T074 must provide this production dashboard; a mock dashboard is not rendering evidence.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var model = document.RootElement;
        var panels = Panels(model).Where(p => p.GetProperty("type").GetString() != "row").ToArray();
        panels.ShouldNotBeEmpty();
        var panelIds = panels.Select(p => p.GetProperty("id").GetInt32()).ToArray();
        panelIds.Distinct().Count().ShouldBe(panelIds.Length);
        RequireScript();
        // Real datasource provisioning/data must accompany these dashboards before rendering can pass.
        var statePath = Environment.GetEnvironmentVariable("TELEMETRY_TEST_GRAFANA_RENDER_STATE");
        File.Exists(statePath).ShouldBeTrue("Provide private render state from the isolated T071 + T069/T070 fixture: grafanaUrl, orgId, serviceToken, variables.");
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath!));
        var settings = state.RootElement.GetProperty(audience);
        using var client = new HttpClient { BaseAddress = new Uri(settings.GetProperty("grafanaUrl").GetString()!), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", settings.GetProperty("serviceToken").GetString());
        client.DefaultRequestHeaders.Add("X-Grafana-Org-Id", settings.GetProperty("orgId").ToString());
        if (audience == "viewer")
            settings.GetProperty("orgId").GetInt32().ShouldBeGreaterThan(1);
        using var sourcesResponse = await client.GetAsync("/api/datasources");
        sourcesResponse.EnsureSuccessStatusCode();
        using var sources = JsonDocument.Parse(await sourcesResponse.Content.ReadAsStringAsync());
        var sourceTypes = sources.RootElement.EnumerateArray().Select(d => d.GetProperty("type").GetString()).ToArray();
        if (audience == "viewer")
        {
            sourceTypes.ShouldNotBeEmpty();
            sourceTypes.ShouldAllBe(t => t == Plugin);
        }
        else
            sourceTypes.ShouldContain("influxdb");
        var uid = model.GetProperty("uid").GetString()!;
        using var loaded = await client.GetAsync("/api/dashboards/uid/" + Uri.EscapeDataString(uid));
        loaded.EnsureSuccessStatusCode();
        var variables = settings.GetProperty("variables");
        foreach (var panel in panels)
        {
            var targets = ResolveVariables(panel.GetProperty("targets"), variables);
            targets.GetArrayLength().ShouldBeGreaterThan(0);
            // Interpolate configured test variables into production targets; do not replace the query model.
            using var query = await client.PostAsJsonAsync("/api/ds/query", new
            {
                queries = targets,
                from = variables.GetProperty("from").GetString(),
                to = variables.GetProperty("to").GetString()
            });
            query.EnsureSuccessStatusCode();
            using var frames = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
            var results = frames.RootElement.GetProperty("results").EnumerateObject().ToArray();
            results.ShouldNotBeEmpty();
            foreach (var result in results)
            {
                if (result.Value.TryGetProperty("error", out var error))
                    error.ValueKind.ShouldBe(JsonValueKind.Null);
                result.Value.GetProperty("frames").GetArrayLength().ShouldBeGreaterThan(0);
                result.Value.GetProperty("frames").EnumerateArray().ShouldContain(f =>
                    f.GetProperty("data").GetProperty("values").EnumerateArray().Any(v => v.GetArrayLength() > 0));
            }
            var queryString = string.Join("&", variables.EnumerateObject().Select(p => Uri.EscapeDataString(p.Name is "from" or "to" ? p.Name : "var-" + p.Name) + "=" + Uri.EscapeDataString(p.Value.GetString()!)));
            using var response = await client.GetAsync($"/render/d-solo/{Uri.EscapeDataString(uid)}/_?panelId={panel.GetProperty("id").GetInt32()}&width=1000&height=600&{queryString}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
            var png = await response.Content.ReadAsByteArrayAsync();
            png.Length.ShouldBeGreaterThan(1000);
            png.Take(8).ShouldBe(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var output = Path.Combine(Root, "artifacts/validation/T066/render", audience, dashboard);
            Directory.CreateDirectory(output);
            await File.WriteAllBytesAsync(Path.Combine(output, panel.GetProperty("id").GetInt32() + ".png"), png);
        }
    }

    private static JsonElement ResolveVariables(JsonElement model, JsonElement variables)
    {
        if (model.ValueKind == JsonValueKind.Object)
            return JsonSerializer.SerializeToElement(model.EnumerateObject().ToDictionary(p => p.Name, p => ResolveVariables(p.Value, variables)));
        if (model.ValueKind == JsonValueKind.Array)
            return JsonSerializer.SerializeToElement(model.EnumerateArray().Select(v => ResolveVariables(v, variables)).ToArray());
        if (model.ValueKind != JsonValueKind.String)
            return model.Clone();
        var text = model.GetString()!;
        foreach (var variable in variables.EnumerateObject())
        {
            var value = variable.Value.GetString()!;
            text = text.Replace("${" + variable.Name + ":sqlstring}", "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'", StringComparison.Ordinal)
                .Replace("${" + variable.Name + "}", value, StringComparison.Ordinal)
                .Replace("$" + variable.Name, value, StringComparison.Ordinal);
        }
        text.Contains("${", StringComparison.Ordinal).ShouldBeFalse("Supply every dashboard variable in the private fixture render state.");
        return JsonSerializer.SerializeToElement(text);
    }

    private static IEnumerable<JsonElement> Panels(JsonElement model)
    {
        if (!model.TryGetProperty("panels", out var panels))
            yield break;
        foreach (var panel in panels.EnumerateArray())
        {
            yield return panel;
            foreach (var child in Panels(panel))
                yield return child;
        }
    }
    private static void RequireScript() => File.Exists(Path.Combine(Root, "scripts/Provision-GrafanaOrganizations.ps1")).ShouldBeTrue("T071 must implement the production Grafana organization reconciler.");

    private sealed record Request(string Method, string Path, string Authorization, int Organization, string Body);
    private sealed class ProvisioningApi(WebApplication app, string url, string fault, string directory) : IAsyncDisposable
    {
        public string Password { get; } = "test-bootstrap-" + Guid.NewGuid().ToString("N");
        public string Basic => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("bootstrap:" + Password));
        public string[] Presentation { get; } = ["presentation-a-" + Guid.NewGuid().ToString("N"), "presentation-b-" + Guid.NewGuid().ToString("N")];
        public ConcurrentQueue<Request> Requests { get; } = new();
        public Dictionary<string, int> Organizations { get; } = new();
        public Dictionary<string, int> TokenOrganizations { get; } = new();
        public Dictionary<int, JsonElement> Datasources { get; } = new();
        public Dictionary<int, JsonElement> Dashboards { get; } = new();
        private readonly Dictionary<int, HashSet<string>> memberships = new();
        private readonly Dictionary<int, JsonElement> accounts = new();
        private readonly Dictionary<string, int> tokenIds = new();
        private readonly List<string> mintedTokens = new();
        private int nextToken;
        private bool healthy;
        public bool HealthyBeforeMutation { get; private set; } = true;
        public (int, int, int, int, int) Resources => (Organizations.Count, accounts.Count, Datasources.Count, Dashboards.Count, memberships.Values.Sum(m => m.Count));

        public static async Task<ProvisioningApi> StartAsync(string fault = "")
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            ProvisioningApi? api = null;
            app.Run(context => api!.HandleAsync(context));
            await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var directory = Path.Combine(Path.GetTempPath(), "t066-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return api = new(app, url, fault, directory);
        }

        private async Task HandleAsync(HttpContext context)
        {
            var request = context.Request;
            var path = request.Path.Value!;
            var auth = request.Headers.Authorization.ToString();
            var org = int.TryParse(request.Headers["X-Grafana-Org-Id"], out var id) ? id : 1;
            var organizationPath = Regex.Match(path, "^/api/orgs/([0-9]+)");
            if (organizationPath.Success)
                org = int.Parse(organizationPath.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (auth.StartsWith("Bearer ", StringComparison.Ordinal) && TokenOrganizations.TryGetValue(auth[7..], out var tokenOrg))
            {
                if (request.Headers.ContainsKey("X-Grafana-Org-Id") && org != tokenOrg)
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                org = tokenOrg;
            }
            var body = await new StreamReader(request.Body).ReadToEndAsync();
            Requests.Enqueue(new(request.Method, path, auth, org, body));
            if (path == "/api/health")
            {
                context.Response.StatusCode = fault == "unhealthy" ? 503 : 200;
                healthy = fault != "unhealthy";
                await context.Response.WriteAsJsonAsync(new
                {
                    database = "ok",
                    version = fault == "grafana-version" ? "0.0.0" : "12.2.5"
                });
                return;
            }
            if (request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
                HealthyBeforeMutation &= healthy;
            if (path.StartsWith("/api/orgs", StringComparison.Ordinal) && (auth != Basic || fault == "service-token-as-bootstrap"))
            {
                context.Response.StatusCode = 401;
                return;
            }
            if (path.StartsWith("/api/plugins/", StringComparison.Ordinal))
            {
                if (fault == "plugin-missing")
                {
                    context.Response.StatusCode = 404;
                    return;
                }
                await context.Response.WriteAsJsonAsync(new
                {
                    enabled = false,
                    signature = fault == "plugin-unsigned" ? "unsigned" : "valid",
                    info = new
                    {
                        version = fault == "plugin-version" ? "0.0.0" : "4.1.1"
                    }
                });
                return;
            }
            if (path == "/api/orgs" && request.Method == "GET")
            {
                await context.Response.WriteAsJsonAsync(Organizations.Select(o => new { name = o.Key, id = o.Value }));
                return;
            }
            if (path.StartsWith("/api/orgs/name/", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path[15..]);
                if (!Organizations.TryGetValue(name, out id))
                {
                    context.Response.StatusCode = 404;
                    return;
                }
                await context.Response.WriteAsJsonAsync(new
                {
                    id,
                    name
                });
                return;
            }
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var data = json.RootElement;
            if (path == "/api/orgs" && request.Method == "POST")
            {
                var name = data.GetProperty("name").GetString()!;
                if (!Organizations.TryGetValue(name, out id))
                    Organizations[name] = id = Organizations.Count + 2;
                await context.Response.WriteAsJsonAsync(new
                {
                    orgId = id,
                    message = "Organization created"
                });
                return;
            }
            if (path.Contains("/users", StringComparison.Ordinal))
            {
                if (!memberships.TryGetValue(org, out var members))
                    memberships[org] = members = new();
                if (request.Method == "GET")
                    await context.Response.WriteAsJsonAsync(members.Select(m => new { userId = 42, login = m, email = m, role = "Viewer" }));
                else
                {
                    var login = data.GetProperty("loginOrEmail").GetString()!;
                    members.Add(login);
                    await context.Response.WriteAsJsonAsync(new
                    {
                        message = "User added",
                        userId = 42
                    });
                }
                return;
            }
            if (path == "/api/org")
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    id = fault == "organization-mismatch" ? 999 : org,
                    name = "Viewer"
                });
                return;
            }
            if (path == "/api/serviceaccounts/search")
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    totalCount = accounts.ContainsKey(org) ? 1 : 0,
                    serviceAccounts = accounts.TryGetValue(org, out var account) ? new[] { account } : []
                });
                return;
            }
            if (path == "/api/serviceaccounts" && request.Method == "POST")
            {
                var value = JsonSerializer.SerializeToElement(new
                {
                    id = org,
                    name = data.GetProperty("name").GetString(),
                    role = data.GetProperty("role").GetString(),
                    orgId = org,
                    isDisabled = false
                });
                accounts[org] = value;
                await context.Response.WriteAsJsonAsync(value);
                return;
            }
            if (path.StartsWith("/api/serviceaccounts/", StringComparison.Ordinal) && path.EndsWith("/tokens", StringComparison.Ordinal))
            {
                if (request.Method == "GET")
                {
                    await context.Response.WriteAsJsonAsync(TokenOrganizations.Where(t => t.Value == org).Select(t => new { id = tokenIds[t.Key], name = "reconciler" }));
                    return;
                }
                var key = "gsa-test-" + org + "-" + Guid.NewGuid().ToString("N");
                TokenOrganizations[key] = org;
                tokenIds[key] = ++nextToken;
                mintedTokens.Add(key);
                await context.Response.WriteAsJsonAsync(new
                {
                    id = tokenIds[key],
                    key,
                    name = "reconciler"
                });
                return;
            }
            if (request.Method == "DELETE" && path.StartsWith("/api/serviceaccounts/", StringComparison.Ordinal) && path.Contains("/tokens/", StringComparison.Ordinal))
            {
                var tokenId = int.Parse(path.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture);
                var key = tokenIds.Single(t => t.Value == tokenId).Key;
                TokenOrganizations.Remove(key);
                tokenIds.Remove(key);
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Token deleted"
                });
                return;
            }
            if (path.StartsWith("/api/datasources", StringComparison.Ordinal) || path == "/api/dashboards/db")
            {
                if (!auth.StartsWith("Bearer gsa-test-", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                if (path.EndsWith("/health", StringComparison.Ordinal))
                {
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = "OK",
                        message = "healthy"
                    });
                    return;
                }
                var store = path == "/api/dashboards/db" ? Dashboards : Datasources;
                if (request.Method == "GET")
                {
                    if (path == "/api/datasources")
                        await context.Response.WriteAsJsonAsync(store.TryGetValue(org, out var item) ? new[] { item } : []);
                    else if (store.TryGetValue(org, out var item))
                        await context.Response.WriteAsJsonAsync(item);
                    else
                        context.Response.StatusCode = 404;
                    return;
                }
                store[org] = data.Clone();
                await context.Response.WriteAsJsonAsync(new
                {
                    id = org,
                    uid = data.TryGetProperty("uid", out var uid) ? uid.GetString() : "fixture-dashboard",
                    status = "success",
                    datasource = data
                });
                return;
            }
            context.Response.StatusCode = 404;
        }

        public async Task RunAsync(bool success)
        {
            if (fault == "bootstrap")
                Presentation[0] = Password;
            if (fault == "other-viewer")
                Presentation[1] = Presentation[0];
            var dashboards = Path.Combine(directory, "dashboards");
            Directory.CreateDirectory(dashboards);
            await File.WriteAllTextAsync(Path.Combine(dashboards, "fixture.json"), """{"uid":"fixture-dashboard","title":"Fixture only","schemaVersion":41,"panels":[]}""");
            var config = new
            {
                bootstrap = new
                {
                    usernameEnv = "T066_BOOTSTRAP_USER",
                    passwordEnv = "T066_BOOTSTRAP_PASSWORD"
                },
                readinessTimeoutSeconds = 2,
                organizations = Enumerable.Range(0, 2).Select(i => new
                {
                    name = "Viewer " + (char)('A' + i),
                    companyIds = new[] { "company-" + (char)('a' + i) },
                    members = new[] { new { login = "viewer-" + i, role = "Viewer" } },
                    serviceAccount = new
                    {
                        name = "reconciler-" + i,
                        role = "Admin"
                    },
                    presentation = new
                    {
                        baseUrl = "https://presentation.invalid",
                        credentialEnv = "T066_PRESENTATION_" + i,
                        permissions = new[] { "telemetry.read", "driver.read" }
                    },
                    datasourceUid = "viewer-api-" + i,
                    dashboardDirectory = dashboards
                })
            };
            var configPath = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("TELEMETRY_TEST_POWERSHELL") ?? "pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(Root, "scripts/Provision-GrafanaOrganizations.ps1"), "-GrafanaUrl", url, "-ConfigurationPath", configPath, "-StateDirectory", Path.Combine(directory, "state") })
                start.ArgumentList.Add(argument);
            start.Environment["T066_BOOTSTRAP_USER"] = "bootstrap";
            start.Environment["T066_BOOTSTRAP_PASSWORD"] = Password;
            for (var i = 0; i < 2; i++)
                start.Environment["T066_PRESENTATION_" + i] = Presentation[i];
            using var process = Process.Start(start).ShouldNotBeNull();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout + await stderr;
            foreach (var secret in Presentation.Concat(mintedTokens).Append(Password).Append(Basic))
                output.ShouldNotContain(secret);
            (process.ExitCode == 0).ShouldBe(success, "Production reconciler must converge or fail closed without secrets in diagnostics.");
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class GrafanaRuntimeTests : IClassFixture<GrafanaRuntimeFixture>
{
    private readonly GrafanaRuntimeFixture fixture;
    public GrafanaRuntimeTests(GrafanaRuntimeFixture fixture) => this.fixture = fixture;
    [Fact]
    public async Task Baked_plugin_is_registered_signed_and_exactly_the_pinned_version()
    {
        using var health = await fixture.Server.JsonAsync("/api/health");
        health.RootElement.GetProperty("version").GetString().ShouldBe("12.2.5");
        using var plugin = await fixture.Server.JsonAsync("/api/plugins/" + GrafanaProvisioningTests.Plugin + "/settings");
        // Datasource plugin registration is proved by metadata plus real backend health,
        // not the app-plugin `enabled` setting (false for this datasource on Grafana 12).
        plugin.RootElement.GetProperty("info").GetProperty("version").GetString().ShouldBe("4.1.1");
        plugin.RootElement.GetProperty("signature").GetString().ShouldBe("valid");
    }
    [Theory]
    [InlineData("/api/health", true)]
    [InlineData("/api/not-a-health-endpoint", false)]
    public async Task Infinity_backend_health_checks_an_actual_URL_and_detects_failure(string path, bool healthy)
    {
        var uid = "t066-" + Guid.NewGuid().ToString("N");
        using var created = await fixture.Server.Client.PostAsJsonAsync("/api/datasources", new
        {
            uid,
            name = uid,
            type = GrafanaProvisioningTests.Plugin,
            access = "proxy",
            jsonData = new
            {
                auth_method = "none",
                allowedHosts = new[] { "http://localhost:3000" },
                customHealthCheckEnabled = true,
                customHealthCheckUrl = "http://localhost:3000" + path
            }
        });
        created.EnsureSuccessStatusCode();
        using var response = await fixture.Server.Client.GetAsync("/api/datasources/uid/" + uid + "/health");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        (result.RootElement.GetProperty("status").GetString() == "OK").ShouldBe(healthy);
    }

    [Fact]
    public async Task Real_Grafana_org_local_service_account_cannot_call_the_Basic_only_admin_organization_API()
    {
        using var organization = await fixture.Server.Client.PostAsJsonAsync("/api/orgs", new
        {
            name = "t066-provider-" + Guid.NewGuid().ToString("N")
        });
        organization.EnsureSuccessStatusCode();
        using var org = JsonDocument.Parse(await organization.Content.ReadAsStringAsync());
        var orgId = org.RootElement.GetProperty("orgId").GetInt32();
        using var accountRequest = new HttpRequestMessage(HttpMethod.Post, "/api/serviceaccounts")
        {
            Content = JsonContent.Create(new { name = "t066-reconciler", role = "Admin" })
        };
        accountRequest.Headers.Add("X-Grafana-Org-Id", orgId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var accountResponse = await fixture.Server.Client.SendAsync(accountRequest);
        accountResponse.EnsureSuccessStatusCode();
        using var account = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync());
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/serviceaccounts/{account.RootElement.GetProperty("id").GetInt32()}/tokens")
        {
            Content = JsonContent.Create(new { name = "t066-token", secondsToLive = 300 })
        };
        tokenRequest.Headers.Add("X-Grafana-Org-Id", orgId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var tokenResponse = await fixture.Server.Client.SendAsync(tokenRequest);
        tokenResponse.EnsureSuccessStatusCode();
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        using var client = new HttpClient { BaseAddress = fixture.Server.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.RootElement.GetProperty("key").GetString());
        using var currentResponse = await client.GetAsync("/api/org");
        currentResponse.EnsureSuccessStatusCode();
        using var current = JsonDocument.Parse(await currentResponse.Content.ReadAsStringAsync());
        current.RootElement.GetProperty("id").GetInt32().ShouldBe(orgId);
        using var adminResponse = await client.GetAsync("/api/orgs");
        adminResponse.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public async Task Real_Grafana_never_exports_the_presentation_bearer_secret_from_a_datasource()
    {
        var secret = "t066-presentation-" + Guid.NewGuid().ToString("N");
        var uid = "s-" + Guid.NewGuid().ToString("N");
        using var created = await fixture.Server.Client.PostAsJsonAsync("/api/datasources", new
        {
            uid,
            name = uid,
            type = GrafanaProvisioningTests.Plugin,
            access = "proxy",
            jsonData = new
            {
                auth_method = "bearerToken",
                allowedHosts = new[] { "https://presentation.invalid" }
            },
            secureJsonData = new
            {
                bearerToken = secret
            }
        });
        created.EnsureSuccessStatusCode();
        var exported = await fixture.Server.Client.GetStringAsync("/api/datasources/uid/" + uid);
        exported.ShouldNotContain(secret);
        using var json = JsonDocument.Parse(exported);
        json.RootElement.GetProperty("secureJsonFields").GetProperty("bearerToken").GetBoolean().ShouldBeTrue();
    }
}

public sealed class GrafanaRuntimeFixture : IAsyncLifetime
{
    internal GrafanaServer Server { get; private set; } = null!;
    public async Task InitializeAsync() => Server = await GrafanaServer.StartAsync();
    public async Task DisposeAsync()
    {
        if (Server is not null)
            await Server.DisposeAsync();
    }
}

internal sealed class GrafanaServer(IFutureDockerImage image, IContainer container, HttpClient client) : IAsyncDisposable
{
    public HttpClient Client => client;
    public static async Task<GrafanaServer> StartAsync(string? provisioning = null)
    {
        var image = new ImageFromDockerfileBuilder().WithName("centralmobilidade-t066-" + Guid.NewGuid().ToString("N"))
            .WithDockerfileDirectory(Path.Combine(GrafanaProvisioningTests.Root, "deploy/grafana")).WithDockerfile("Dockerfile").Build();
        try
        {
            await image.CreateAsync();
            var password = "t066-" + Guid.NewGuid().ToString("N");
            var builder = new ContainerBuilder(image).WithPortBinding(3000, true)
                .WithEnvironment("GF_SECURITY_ADMIN_USER", "bootstrap").WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", password)
                .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "false").WithEnvironment("GF_PLUGINS_PREINSTALL_DISABLED", "true")
                .WithEnvironment("GF_ANALYTICS_REPORTING_ENABLED", "false").WithEnvironment("GF_ANALYTICS_CHECK_FOR_UPDATES", "false")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3000).ForPath("/api/health")));
            if (provisioning is not null)
            {
                builder = builder.WithResourceMapping(provisioning, "/etc/grafana/provisioning");
                var dashboards = Path.Combine(GrafanaProvisioningTests.Root, "deploy/grafana/dashboards/internal");
                if (Directory.Exists(dashboards))
                    builder = builder.WithResourceMapping(dashboards, "/var/lib/grafana/dashboards/internal");
            }
            var container = builder.Build();
            try
            {
                await container.StartAsync();
                var client = new HttpClient { BaseAddress = new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(3000)}"), Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("bootstrap:" + password)));
                return new(image, container, client);
            }
            catch { await container.DisposeAsync(); throw; }
        }
        catch { await image.DisposeAsync(); throw; }
    }
    public async Task<JsonDocument> JsonAsync(string path) => JsonDocument.Parse(await client.GetStringAsync(path));
    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await container.DisposeAsync();
        await image.DisposeAsync();
    }
}
