using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Serilog;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Observability;

[Collection("Observability")]
public sealed class LokiIngestionTests
{
    private static string ConfigPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(directory.FullName, "CentralMobilidade.slnx")))
                directory = directory.Parent ?? throw new InvalidOperationException("Repository root missing.");
            return Path.Combine(directory.FullName, "deploy/loki/loki-config.yaml");
        }
    }

    private static IContainer Container() => new ContainerBuilder("grafana/loki:3.7.0")
        .WithResourceMapping(ConfigPath, "/etc/loki/")
        .WithCommand("-config.file=/etc/loki/loki-config.yaml")
        .WithPortBinding(3100, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3100).ForPath("/ready")))
        .Build();

    [Fact]
    public async Task Shipped_retention_config_is_valid_and_rejects_logs_older_than_thirty_days()
    {
        await using var loki = Container();
        await loki.StartAsync();
        var validation = await loki.ExecAsync(["/usr/bin/loki", "-config.file=/etc/loki/loki-config.yaml", "-verify-config=true"]);
        validation.ExitCode.ShouldBe(0, validation.Stderr);
        using var client = Client(loki);
        var config = await client.GetStringAsync("/config");
        config.ShouldMatch(@"retention_period: (720h(0m0s)?|30d|4w2d)");
        config.ShouldContain("retention_enabled: true");
        config.ShouldContain("working_directory: /loki/compactor");
        config.ShouldContain("delete_request_store: filesystem");
        config.ShouldContain("active_index_directory: /loki/tsdb-shipper-active");
        config.ShouldContain("cache_location: /loki/tsdb-shipper-cache");
        using var stale = await PushAsync(client, "api", DateTimeOffset.UtcNow.AddDays(-31), "Operational event");
        stale.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await stale.Content.ReadAsStringAsync()).ShouldContain("too old");
        using var retained = await PushAsync(client, "worker", DateTimeOffset.UtcNow.AddDays(-29), "retained-operational-event");
        retained.IsSuccessStatusCode.ShouldBeTrue(await retained.Content.ReadAsStringAsync());
        using var current = await PushAsync(client, "api", DateTimeOffset.UtcNow, "current-operational-event");
        current.IsSuccessStatusCode.ShouldBeTrue(await current.Content.ReadAsStringAsync());
        await EventuallyAsync(async () => (await QueryAsync(client, "api")).Contains("current-operational-event", StringComparison.Ordinal));
        // WAL/storage live under /loki. Restart this same disposable container only.
        await loki.StopAsync();
        await loki.StartAsync();
        using var restarted = Client(loki);
        await EventuallyAsync(async () => (await QueryAsync(restarted, "api")).Contains("current-operational-event", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Otlp_indexes_only_service_and_drops_seeded_attributes_instead_of_storing_metadata()
    {
        await using var loki = Container();
        await loki.StartAsync();
        using var client = Client(loki);
        using var output = new StringWriter();
        using (var logger = PrivacyLoggingPolicy.CreateLogger(TelemetryServiceRole.Api, output))
            logger.ForContext("BatchId", Guid.NewGuid()).Error(new InvalidOperationException("SEEDED_EXCEPTION"),
                "SEEDED_DRIVER {Plate} {Token}", "ABC1D23", "SEEDED_TOKEN");
        var body = output.ToString().Trim();
        var seeds = new[] { "ABC1D23", "12345678900", "-23.123456", "SEEDED_TOKEN", "SEEDED_DRIVER", "SEEDED_EXCEPTION", "SEEDED_COMPANY", "SEEDED_BATCH", "SEEDED_INSTANCE" };
        object Attribute(string key, string value) => new
        {
            key,
            value = new
            {
                stringValue = value
            }
        };
        var attributes = seeds.Select((seed, index) => Attribute("private." + index, seed)).ToArray();
        var payload = new
        {
            resourceLogs = new[]
            {
                new
                {
                    resource = new { attributes = attributes.Concat(new[] { Attribute("service.name", "api"), Attribute("service.instance.id", "SEEDED_INSTANCE"), Attribute("k8s.pod.name", "SEEDED_COMPANY") }).ToArray() },
                    scopeLogs = new[]
                    {
                        new
                        {
                            scope = new { name = "telemetry", attributes },
                            logRecords = new[] { new { timeUnixNano = Nanos(DateTimeOffset.UtcNow), severityNumber = 17, body = new { stringValue = body }, attributes } }
                        }
                    }
                }
            }
        };
        using var content = JsonContent.Create(payload);
        content.Headers.ContentType!.CharSet = null; // Loki's OTLP decoder requires the exact media type.
        using var pushed = await client.PostAsync("/otlp/v1/logs", content);
        pushed.IsSuccessStatusCode.ShouldBeTrue(await pushed.Content.ReadAsStringAsync());
        await EventuallyAsync(async () => (await QueryAsync(client, "api")).Contains("Operational event", StringComparison.Ordinal));
        var query = await QueryAsync(client, "api");
        using var json = JsonDocument.Parse(query);
        var result = json.RootElement.GetProperty("data").GetProperty("result");
        result.GetArrayLength().ShouldBe(1);
        var labels = await client.GetStringAsync("/loki/api/v1/labels");
        // LogQL merges structured metadata into response labels; /series is the index boundary.
        using var series = JsonDocument.Parse(await client.GetStringAsync("/loki/api/v1/series?match%5B%5D=%7Bservice_name%3D%22api%22%7D"));
        series.RootElement.GetProperty("data")[0].EnumerateObject().Select(p => p.Name).ShouldBe(new[] { "service_name" });
        foreach (var seed in seeds)
        {
            query.ShouldNotContain(seed);
            labels.ShouldNotContain(seed);
        }
        query.ShouldNotContain("private_");
        query.ShouldNotContain("service_instance_id");
        // Negative control: native push bypasses OTLP filtering, including an arbitrary label.
        using var native = await client.PostAsJsonAsync("/loki/api/v1/push", new
        {
            streams = new[] { new { stream = new { service_name = "worker", arbitrary = "synthetic-native" }, values = new[] { new[] { Nanos(DateTimeOffset.UtcNow), "Operational event" } } } }
        });
        native.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // service_name is excluded from Loki's label-count budget; two extra labels exceed it.
        using var excessive = await client.PostAsJsonAsync("/loki/api/v1/push", new
        {
            streams = new[] { new { stream = new { service_name = "worker", batch_id = "synthetic-id", trace_id = "synthetic-trace" }, values = new[] { new[] { Nanos(DateTimeOffset.UtcNow), "Operational event" } } } }
        });
        excessive.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await excessive.Content.ReadAsStringAsync()).ShouldContain("label names");
    }

    private static HttpClient Client(IContainer loki) => new()
    {
        BaseAddress = new Uri($"http://{loki.Hostname}:{loki.GetMappedPublicPort(3100)}"),
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static string Nanos(DateTimeOffset time) => (time.ToUnixTimeMilliseconds() * 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Task<HttpResponseMessage> PushAsync(HttpClient client, string service, DateTimeOffset time, string body) => client.PostAsJsonAsync("/loki/api/v1/push", new
    {
        streams = new[] { new { stream = new { service_name = service }, values = new[] { new[] { Nanos(time), body } } } }
    });

    private static Task<string> QueryAsync(HttpClient client, string service) => client.GetStringAsync("/loki/api/v1/query_range?query=" + Uri.EscapeDataString("{service_name=\"" + service + "\"}") + "&start=" + Nanos(DateTimeOffset.UtcNow.AddHours(-1)) + "&end=" + Nanos(DateTimeOffset.UtcNow));

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (await condition())
                return;
            await Task.Delay(500);
        }
        Assert.Fail("Loki ingestion/query condition was not met within 60 seconds.");
    }
}
