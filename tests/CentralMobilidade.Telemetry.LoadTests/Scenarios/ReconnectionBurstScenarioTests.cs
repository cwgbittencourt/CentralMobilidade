using System;
using System.Collections.Concurrent;
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

public sealed class ReconnectionBurstScenarioTests
{
    [Fact]
    public void Reference_schedule_covers_exactly_one_hour_for_each_of_ten_thousand_vehicles()
    {
        var profile = BurstProfile.Target;
        var counts = new int[10000];
        long ordinal = 0;
        for (var batch = 0; batch < 6000; batch++)
        {
            profile.FirstOrdinal(batch).ShouldBe(ordinal);
            profile.EventsInBatch(batch).ShouldBe(300);
            for (var point = 0; point < 300; point++, ordinal++)
                counts[ordinal % 10000]++;
        }
        ordinal.ShouldBe(1800000);
        counts.ShouldAllBe(count => count == 180);
        profile.EventsInBatch(6000).ShouldBe(0);
        profile.RecoverySeconds.ShouldBe(1200);
    }

    [Theory]
    [InlineData(0, 3600, 800)]
    [InlineData(5999, 20, 800)]
    [InlineData(6000, 0, 500)]
    public void Mixed_gzip_has_disjoint_live_and_historical_identities(int batch, int ageSeconds, int count)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1791686400000);
        var body = ReconnectionBurstScenario.CreateBatch("fixture", batch, start, BurstProfile.Target);
        var lines = Decode(body.Gzip).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(count);
        lines.Take(500).ShouldAllBe(line => !line.Contains("offline-v", StringComparison.Ordinal));
        if (count > 500)
        {
            lines.Skip(500).ShouldAllBe(line => line.Contains("veiculo_id=offline-v", StringComparison.Ordinal));
            lines[^1].ShouldEndWith((start.ToUnixTimeMilliseconds() - ageSeconds * 1000L).ToString(CultureInfo.InvariantCulture));
        }
        body.RawBytes.ShouldBe(Encoding.UTF8.GetByteCount(Decode(body.Gzip)));
        body.Gzip.Length.ShouldBeLessThan(8 * 1024 * 1024);
    }

    [Fact]
    public async Task Public_runner_rejects_a_shortened_reference_burst_before_traffic()
    {
        using var handler = new ProtocolHandler("ok");
        using var http = new HttpClient(handler);
        await Should.ThrowAsync<ArgumentException>(async () => await ReconnectionBurstScenario.RunAsync(Options(), http, "private-token"));
        handler.Counts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("lost")]
    [InlineData("pending")]
    [InlineData("late")]
    public async Task Scaled_protocol_drill_keeps_normal_traffic_and_enforces_burst_deadline(string mode)
    {
        using var handler = new ProtocolHandler(mode);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var options = Options();
        var report = await ReconnectionBurstScenario.RunAsync(options, http, "private-token", new BurstProfile(10, 3, 1, 1, false));
        report.ReferenceCapacityRun.ShouldBeFalse();
        report.ScheduledEvents.ShouldBe(10030); // 5,000/s for two seconds, plus 30 delayed events.
        report.AttemptedEvents.ShouldBe(10030);
        report.AcknowledgedEvents.ShouldBe(10030);
        report.Reconnection!.ScheduledEvents.ShouldBe(30);
        report.Reconnection.AcknowledgedEvents.ShouldBe(30);
        handler.Counts.Values.Count(count => count == 503).ShouldBe(10);
        handler.Counts.Values.Count(count => count == 500).ShouldBe(10);
        handler.NormalPostsWhileObserving.ShouldBeGreaterThan(0);
        if (mode == "ok")
        {
            report.Passed.ShouldBeTrue();
            report.ReconciledEvents.ShouldBe(10030);
            report.Reconnection.DrainedEvents.ShouldBe(30);
            report.Reconnection.ObservedDrainSecondsAfterBurst!.Value.ShouldBeLessThanOrEqualTo(1);
        }
        else
        {
            report.Passed.ShouldBeFalse();
            report.Failures.ShouldContain("burst_not_drained_by_deadline");
            report.Reconnection.ObservedDrainSecondsAfterBurst.ShouldBeNull();
        }
        var json = await File.ReadAllTextAsync(Directory.GetFiles(options.ReportDirectory, "reconnect-*.json").Single());
        json.ShouldNotContain("private-token");
    }

    private static SteadyStateOptions Options()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CentralMobilidade.slnx")))
            root = root.Parent;
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root!.FullName, "artifacts", "validation", "T092", id);
        return new(new("http://sut/"), new("http://metrics/"), directory,
            SteadyStateScenario.RequiredMetrics.ToDictionary(key => key, key => key), DurationSeconds: 1, DrainSeconds: 1);
    }
    private static string Decode(byte[] bytes)
    {
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    private sealed class ProtocolHandler(string mode) : HttpMessageHandler
    {
        public readonly ConcurrentDictionary<Guid, int> Counts = new();
        public int NormalPostsWhileObserving;
        private int observing;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "metrics")
            {
                request.Headers.Authorization.ShouldBeNull();
                var key = Uri.UnescapeDataString(request.RequestUri.Query[7..]);
                var number = key switch
                {
                    "referenceCpuCores" => 8d,
                    "referenceMemoryBytes" => 16d * 1024 * 1024 * 1024,
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
            request.Headers.Authorization!.Parameter.ShouldBe("private-token");
            if (request.Method == HttpMethod.Post)
            {
                var count = Decode(await request.Content!.ReadAsByteArrayAsync(token)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                if (count == 500 && Volatile.Read(ref observing) == 1)
                    Interlocked.Increment(ref NormalPostsWhileObserving);
                var id = Guid.NewGuid();
                Counts[id] = count;
                return Json(new BatchAccepted(id, DateTimeOffset.UtcNow, "/unused"), HttpStatusCode.Accepted);
            }
            var batch = Guid.Parse(request.RequestUri.Segments[^1]);
            var events = Counts[batch];
            if (events > 500)
            {
                Interlocked.Exchange(ref observing, 1);
                if (mode == "late")
                    await Task.Delay(TimeSpan.FromMilliseconds(1200), token);
            }
            var state = events > 500 && mode == "pending" ? BatchState.Processing : events > 500 && mode == "lost" ? BatchState.Failed : BatchState.Completed;
            return Json(new BatchStatus(batch, state, DateTimeOffset.UtcNow, state == BatchState.Completed ? events : 0, 0, 0, TotalLines: events));
        }
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK)
            => new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
    }
}
