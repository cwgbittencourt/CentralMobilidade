using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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

public sealed class SteadyStateScenarioTests
{
    [Fact]
    public async Task Ten_thousand_vehicle_baseline_offers_five_hundred_events_per_second()
    {
        var directory = Path.Combine(Root(), "artifacts", "validation", "T094", "baseline-protocol-" + Guid.NewGuid().ToString("N"));
        using var handler = new ProtocolHandler { BatchEvents = 50 };
        using var http = new HttpClient(handler);
        var report = await SteadyStateScenario.RunAsync(OptionsFor(directory) with
        {
            Vehicles = 10000
        }, http, "private-token");
        report.Passed.ShouldBeTrue();
        report.TargetEventsPerSecond.ShouldBe(500);
        report.ScheduledEvents.ShouldBe(500);
        report.ReconciledEvents.ShouldBe(500);
        handler.InvalidBody.ShouldBeFalse();
    }

    [Theory]
    [InlineData(10000, 50)]
    [InlineData(100000, 500)]
    public void Each_profile_preserves_twenty_second_vehicle_cadence(int vehicles, int count)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1791686400000);
        var first = Decode(SteadyStateScenario.CreateBatch("baseline", 0, start, vehicles).Gzip);
        first.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(count);
        Decode(SteadyStateScenario.CreateBatch("baseline", 200, start, vehicles).Gzip)
            .ShouldBe(first.Replace("1791686400000", "1791686420000", StringComparison.Ordinal));
        Should.Throw<ArgumentOutOfRangeException>(() => SteadyStateScenario.CreateBatch("baseline", 0, start, 50000));
    }
    private static SteadyStateOptions OptionsFor(string directory) => new(new("http://sut/"), new("http://metrics/"), directory,
        SteadyStateScenario.RequiredMetrics.ToDictionary(key => key, key => key), DurationSeconds: 1, DrainSeconds: 1);

    [Fact]
    public void Percentiles_use_nearest_rank_and_empty_is_not_zero_latency()
    {
        var actual = SteadyStateScenario.Percentiles(Enumerable.Range(1, 100).Select(value => (double)value))!;
        actual.ShouldBe(new(50, 95, 99));
        SteadyStateScenario.Percentiles([]).ShouldBeNull();
    }

    [Fact]
    public void Generator_models_one_hundred_thousand_vehicles_at_twenty_second_intervals()
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1791686400000);
        var first = Decode(SteadyStateScenario.CreateBatch("fixture", 0, start).Gzip);
        var next = Decode(SteadyStateScenario.CreateBatch("fixture", 200, start).Gzip);
        first.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(500);
        next.ShouldBe(first.Replace("1791686400000", "1791686420000", StringComparison.Ordinal));
        first.ShouldContain("empresa_id=load-fixture-0,veiculo_id=v0 ");
        var payload = SteadyStateScenario.CreateBatch("fixture", 0, start);
        payload.RawBytes.ShouldBe(Encoding.UTF8.GetByteCount(first));
        payload.Gzip.Length.ShouldBeLessThan(8 * 1024 * 1024);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Invalid_duration_fails_before_network(int duration)
        => Should.Throw<ArgumentException>(() => SteadyStateScenario.Validate(OptionsFor(Path.GetTempPath()) with { DurationSeconds = duration }, "private-token"));

    [Fact]
    public void Incomplete_monitoring_is_not_a_zero_resource_reading()
    {
        var options = OptionsFor(Path.GetTempPath());
        options.Queries.Remove("memoryBytes");
        Should.Throw<ArgumentException>(() => SteadyStateScenario.Validate(options, "private-token"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("stale")]
    [InlineData("nan")]
    [InlineData("profile")]
    public async Task Invalid_monitoring_prevents_any_intake(string fault)
    {
        using var handler = new ProtocolHandler { MetricFault = fault };
        using var http = new HttpClient(handler);
        await Should.ThrowAsync<Exception>(async () => await SteadyStateScenario.RunAsync(OptionsFor(Path.GetTempPath()), http, "private-token"));
        handler.Posts.ShouldBe(0);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("reject")]
    [InlineData("partial")]
    [InlineData("cpu")]
    [InlineData("memory")]
    [InlineData("duplicate")]
    public async Task Protocol_drill_reconciles_or_reports_failures_without_claiming_capacity(string mode)
    {
        var directory = Path.Combine(Root(), "artifacts", "validation", "T091", "protocol-" + Guid.NewGuid().ToString("N"));
        using var handler = new ProtocolHandler { Mode = mode };
        using var http = new HttpClient(handler);
        var report = await SteadyStateScenario.RunAsync(OptionsFor(directory), http, "private-token");
        report.ReferenceCapacityRun.ShouldBeFalse();
        report.ScheduledEvents.ShouldBe(5000);
        report.AttemptedEvents.ShouldBe(5000);
        report.Samples.Count.ShouldBeGreaterThanOrEqualTo(2);
        if (mode == "ok")
        {
            report.Passed.ShouldBeTrue();
            report.AcknowledgedEvents.ShouldBe(5000);
            report.ReconciledEvents.ShouldBe(5000);
            report.IntakeMilliseconds.ShouldNotBeNull();
            report.TerminalObservationMilliseconds.ShouldNotBeNull();
        }
        else
        {
            report.Passed.ShouldBeFalse();
            report.Failures.ShouldContain(mode switch
            {
                "reject" => "intake_not_202",
                "partial" => "unexpected_terminal_outcome",
                "cpu" => "cpu_limit_exceeded",
                "duplicate" => "duplicate_receipt",
                _ => "application_memory_limit_exceeded"
            });
        }
        handler.Posts.ShouldBe(10);
        handler.InvalidBody.ShouldBeFalse();
        (await File.ReadAllTextAsync(Directory.GetFiles(directory, "steady-*.json").Single())).ShouldNotContain("private-token");
    }

    private static string Decode(byte[] bytes)
    {
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CentralMobilidade.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException();
    }

    private sealed class ProtocolHandler : HttpMessageHandler
    {
        public string Mode = "ok", MetricFault = "";
        public int Posts;
        public int BatchEvents = 500;
        public bool InvalidBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "metrics")
            {
                var key = Uri.UnescapeDataString(request.RequestUri.Query[7..]);
                var number = key switch
                {
                    "referenceCpuCores" => MetricFault == "profile" ? 4d : 8d,
                    "referenceMemoryBytes" => 16d * 1024 * 1024 * 1024,
                    "cpuPercent" => Mode == "cpu" && Posts > 0 ? 86d : 20d,
                    "memoryBytes" => Mode == "memory" && Posts > 0 ? 13d * 1024 * 1024 * 1024 : 1024d,
                    _ => 1d
                };
                var value = MetricFault == "nan" ? "NaN" : number.ToString(CultureInfo.InvariantCulture);
                object[] result = MetricFault == "empty" ? [] : [new { metric = new { }, value = new object[]
                { DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d - (MetricFault == "stale" ? 100 : 0), value } }];
                return Json(new
                {
                    status = "success",
                    data = new
                    {
                        resultType = "vector",
                        result
                    }
                });
            }
            request.Headers.Authorization!.Parameter.ShouldBe("private-token");
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref Posts);
                var text = Decode(await request.Content!.ReadAsByteArrayAsync(token));
                InvalidBody |= text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != BatchEvents
                    || request.Content.Headers.ContentEncoding.Single() != "gzip" || request.Headers.GetValues("X-Contract-Version").Single() != "v1";
                if (Mode == "reject")
                    return new(HttpStatusCode.TooManyRequests);
                return Json(new BatchAccepted(Mode == "duplicate" ? Guid.Parse("11111111-1111-1111-1111-111111111111") : Guid.NewGuid(), DateTimeOffset.UtcNow, "/unused"), HttpStatusCode.Accepted);
            }
            var id = Guid.Parse(request.RequestUri.Segments[^1]);
            return Json(new BatchStatus(id, Mode == "partial" ? BatchState.CompletedWithRejections : BatchState.Completed,
                DateTimeOffset.UtcNow, Mode == "partial" ? BatchEvents - 1 : BatchEvents, Mode == "partial" ? 1 : 0, 0, TotalLines: BatchEvents));
        }
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
            => new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
    }
}
