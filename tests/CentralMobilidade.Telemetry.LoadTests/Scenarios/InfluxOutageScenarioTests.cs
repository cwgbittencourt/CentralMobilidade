using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.LoadTests.Scenarios;

public sealed class InfluxOutageScenarioTests
{
    [Fact]
    public void Projection_separates_gzip_disk_and_net_recovery_capacity()
    {
        var result = InfluxOutageScenario.ProjectSevenDays(5000, 1000, 10, 5000, 10000, 1);
        result.Events.ShouldBe(3024000000);
        result.Messages.ShouldBe(6048000);
        result.GzipPayloadBytes.ShouldBe(604800000);
        result.QueueDiskBytes.ShouldBe(3024000000);
        result.CatchUpSecondsWithNormalTraffic.ShouldBe(604800);
        result.Assessment.ShouldBe("estimate_only");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4000)]
    [InlineData(5000)]
    public void No_net_catchup_capacity_is_not_reported_as_zero_drain_time(long recovered)
    {
        var result = InfluxOutageScenario.ProjectSevenDays(5000, 1000, 0, 0, recovered, 1);
        result.QueueDiskBytes.ShouldBeNull();
        result.CatchUpSecondsWithNormalTraffic.ShouldBeNull();
        result.Assessment.ShouldBe("catch_up_not_demonstrated");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    public void Projection_rejects_nonmeasurement_time(double seconds)
        => Should.Throw<ArgumentException>(() => InfluxOutageScenario.ProjectSevenDays(1, 1, 1, 1, 1, seconds));

    [Theory]
    [InlineData("not-isolated")]
    [InlineData("path")]
    [InlineData("missing-metric")]
    public void Incomplete_or_ambiguous_target_is_rejected_before_network(string fault)
    {
        var options = Options();
        options = fault switch
        {
            "not-isolated" => options with { IsolatedTarget = false },
            "path" => options with { ProxyName = "../another" },
            _ => options
        };
        if (fault == "missing-metric")
            options.Load.Queries.Remove("queueDiskUsedBytes");
        Should.Throw<ArgumentException>(() => InfluxOutageScenario.Validate(options, "producer-secret"));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("bypass")]
    [InlineData("no-backlog")]
    [InlineData("lost")]
    [InlineData("restore-fails")]
    [InlineData("ambiguous-disable")]
    public async Task Protocol_drill_proves_fault_effect_reconciliation_and_restoration(string mode)
    {
        var options = Options();
        using var handler = new ProtocolHandler(mode);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var result = await InfluxOutageScenario.RunAsync(options, http, "producer-secret", faultToken: "fault-secret");
        result.ReferenceCapacityRun.ShouldBeFalse();
        result.Passed.ShouldBe(mode == "ok");
        result.ProxyRestored.ShouldBe(mode != "restore-fails");
        handler.Enabled.ShouldBe(mode != "restore-fails");
        handler.RestoreAttempts.ShouldBeGreaterThan(0);
        if (mode == "ok")
        {
            result.Load!.AcknowledgedEvents.ShouldBe(10000);
            result.Load.ReconciledEvents.ShouldBe(10000);
            result.During!.Values["queueMessages"].ShouldBeGreaterThan(0);
            result.After!.Values["queueMessages"].ShouldBe(0);
            result.SevenDays!.Events.ShouldBe(3024000000);
        }
        if (mode == "bypass")
            result.Failures.ShouldContain("writes_confirmed_during_outage_or_counter_reset");
        if (mode == "no-backlog")
            result.Failures.ShouldContain("outage_backlog_not_observed");
        if (mode == "lost")
            result.Failures.ShouldContain("load_or_reconciliation_failed");
        if (mode == "restore-fails")
            result.Failures.ShouldContain("proxy_restore_unconfirmed");
        if (mode == "ambiguous-disable")
            handler.Counts.ShouldBeEmpty();
        var json = await File.ReadAllTextAsync(Directory.GetFiles(options.Load.ReportDirectory, "outage-*.json").Single());
        json.ShouldNotContain("producer-secret");
        json.ShouldNotContain("fault-secret");
    }

    [Fact]
    public async Task Cancellation_still_restores_with_an_independent_token()
    {
        var options = Options();
        using var cancellation = new CancellationTokenSource();
        using var handler = new ProtocolHandler("ok") { OnDisable = () => cancellation.CancelAfter(50) };
        using var http = new HttpClient(handler);
        var report = await InfluxOutageScenario.RunAsync(options, http, "producer-secret", cancellation.Token, "fault-secret");
        report.Passed.ShouldBeFalse();
        report.Failures.ShouldContain("cancelled");
        report.ProxyRestored.ShouldBeTrue();
        handler.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Mismatched_upstream_does_not_mutate_proxy()
    {
        using var handler = new ProtocolHandler("ok");
        using var http = new HttpClient(handler);
        var options = Options() with
        {
            ExpectedUpstream = "wrong:8181"
        };
        await Should.ThrowAsync<InvalidDataException>(async () => await InfluxOutageScenario.RunAsync(options, http, "producer-secret", faultToken: "fault-secret"));
        handler.ControlPosts.ShouldBe(0);
    }

    private static InfluxOutageOptions Options()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CentralMobilidade.slnx")))
            root = root.Parent;
        var directory = Path.Combine(root!.FullName, "artifacts", "validation", "T093", Guid.NewGuid().ToString("N"));
        var load = new SteadyStateOptions(new("http://sut/"), new("http://metrics/"), directory,
            InfluxOutageScenario.RequiredMetrics.ToDictionary(key => key, key => key), DrainSeconds: 1);
        return new(load, new("http://127.0.0.1:8474/"), "test_influx", "[::]:8182", "influxdb:8181", true, 1, 1);
    }

    private sealed class ProtocolHandler(string mode) : HttpMessageHandler
    {
        public volatile bool Enabled = true;
        public int RestoreAttempts, ControlPosts;
        public Action? OnDisable;
        public readonly ConcurrentDictionary<Guid, int> Counts = new();
        private int blockedPosts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "127.0.0.1")
            {
                request.Headers.Authorization!.Parameter.ShouldBe("fault-secret");
                if (request.Method == HttpMethod.Post)
                {
                    Interlocked.Increment(ref ControlPosts);
                    using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    var value = json.RootElement.GetProperty("enabled").GetBoolean();
                    if (value)
                    {
                        Interlocked.Increment(ref RestoreAttempts);
                        if (mode == "restore-fails")
                            return new(HttpStatusCode.ServiceUnavailable);
                    }
                    Enabled = value;
                    if (!value)
                    {
                        OnDisable?.Invoke();
                        if (mode == "ambiguous-disable")
                            throw new HttpRequestException("PRIVATE control failure");
                    }
                }
                return Json(new
                {
                    name = "test_influx",
                    listen = "[::]:8182",
                    upstream = "influxdb:8181",
                    enabled = Enabled
                });
            }
            if (request.RequestUri.Host == "metrics")
            {
                request.Headers.Authorization.ShouldBeNull();
                var key = Uri.UnescapeDataString(request.RequestUri.Query[7..]);
                var number = key switch
                {
                    "referenceCpuCores" => 8d,
                    "referenceMemoryBytes" => 16d * 1024 * 1024 * 1024,
                    "queueMessages" => Enabled || mode == "no-backlog" ? 0 : blockedPosts,
                    "queuePayloadBytes" => Enabled || mode == "no-backlog" ? 0 : blockedPosts * 1000d,
                    "queueDiskUsedBytes" => 10000d + blockedPosts * 1500,
                    "storageWriteFailures" => blockedPosts > 0 ? 1 : 0,
                    "storageWriteConfirmations" => Enabled || mode == "bypass" ? Counts.Count : 0,
                    _ => 1d
                };
                return Json(new
                {
                    status = "success",
                    data = new
                    {
                        resultType = "vector",
                        result = new[] { new { metric = new { }, value = new object[] { DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d, number.ToString(CultureInfo.InvariantCulture) } } }
                    }
                });
            }
            request.Headers.Authorization!.Parameter.ShouldBe("producer-secret");
            if (request.Method == HttpMethod.Post)
            {
                if (!Enabled)
                    Interlocked.Increment(ref blockedPosts);
                var id = Guid.NewGuid();
                Counts[id] = 500;
                return Json(new BatchAccepted(id, DateTimeOffset.UtcNow, "/unused"), HttpStatusCode.Accepted);
            }
            var batch = Guid.Parse(request.RequestUri.Segments[^1]);
            var completed = Enabled && mode != "lost";
            return Json(new BatchStatus(batch, completed ? BatchState.Completed : mode == "lost" ? BatchState.Failed : BatchState.Retrying,
                DateTimeOffset.UtcNow, completed ? 500 : 0, 0, 0, TotalLines: 500));
        }
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
            => new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
    }
}
