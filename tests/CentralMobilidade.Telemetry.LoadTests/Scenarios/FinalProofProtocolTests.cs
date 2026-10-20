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
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.LoadTests.Scenarios;

// Protocol controls only. No simulated response is a real T102 acceptance result.
public sealed class FinalProofProtocolTests
{
    private static readonly FinalProofSecrets Secrets = new("producer-secret", "influx-secret", "internal-secret", "viewer-secret");
    private static FinalProofOptions Options() => new(new("http://api.test/"), new("http://influx.test/"), new("http://grafana.test/"),
        Path.Combine(FinalProofGrafana.Root(), "artifacts/validation/T101/protocol-" + Guid.NewGuid().ToString("N")),
        Final100kGrafanaProofScenarioTests.Run,
        DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds()), true, PollMilliseconds: 1, CompanyLookupUnavailable: true);

    [Fact]
    public async Task Full_protocol_reconciles_every_identity_fields_grafana_frames_and_six_PNGs()
    {
        var options = Options();
        using var handler = new Protocol(options);
        using var http = new HttpClient(handler);
        var report = await Final100kGrafanaProofScenario.RunAsync(options, http, Secrets);
        report.Failures.ShouldBeEmpty();
        report.Passed.ShouldBeTrue();
        report.CapacityApproved.ShouldBeFalse();
        report.AcceptedEvents.ShouldBe(100000);
        report.StoredEvents.ShouldBe(100000);
        report.BatchIds.Distinct().Count().ShouldBe(200);
        report.CorpusSha256.Length.ShouldBe(64);
        handler.Posts.ShouldBe(200);
        handler.SqlPages.ShouldBe(100);
        Directory.GetFiles(options.ReportDirectory, "*.png").Length.ShouldBe(6);
        var output = await File.ReadAllTextAsync(Path.Combine(options.ReportDirectory, "report.json"));
        foreach (var secret in new[] { Secrets.Producer, Secrets.Influx, Secrets.InternalGrafana, Secrets.ViewerGrafana })
            output.ShouldNotContain(secret);
    }

    [Theory]
    [InlineData("not-ready", "api_not_ready")]
    [InlineData("dirty", "namespace_not_empty")]
    [InlineData("viewer-influx", "grafana_datasource_isolation")]
    [InlineData("org", "grafana_org_mismatch")]
    [InlineData("receipt", "invalid_receipt")]
    [InlineData("location", "invalid_receipt")]
    [InlineData("non202", "intake_not_202")]
    [InlineData("duplicate-receipt", "invalid_receipt")]
    [InlineData("status-id", "status_identity_mismatch")]
    [InlineData("rejected", "terminal_counters_mismatch")]
    [InlineData("replayed", "terminal_counters_mismatch")]
    [InlineData("terminal", "batch_not_successful")]
    [InlineData("missing", "stored_count_mismatch")]
    [InlineData("extra", "stored_count_mismatch")]
    [InlineData("duplicate-row", "stored_identity_mismatch")]
    [InlineData("fields", "stored_fields_mismatch")]
    [InlineData("ignition", "stored_fields_mismatch")]
    [InlineData("template", "production_query_mismatch")]
    [InlineData("grafana-error", "grafana_query_error")]
    [InlineData("grafana-empty", "empty_query_frames")]
    [InlineData("latest", "position_or_trip_mismatch")]
    [InlineData("trip", "position_or_trip_mismatch")]
    [InlineData("health", "health_count_mismatch")]
    [InlineData("scope", "viewer_scope_leak")]
    [InlineData("png", "render_not_png")]
    public async Task Incomplete_or_divergent_proofs_fail_closed(string fault, string expected)
    {
        var options = Options();
        using var handler = new Protocol(options, fault);
        using var http = new HttpClient(handler);
        var report = await Final100kGrafanaProofScenario.RunAsync(options, http, Secrets);
        report.Passed.ShouldBeFalse();
        report.Failures.ShouldContain(expected);
        if (fault is "not-ready" or "dirty" or "viewer-influx" or "org")
            handler.Posts.ShouldBe(0);
    }

    [Fact]
    public async Task Pending_forever_expires_and_cancelled_run_keeps_failure_evidence()
    {
        var options = Options() with
        {
            DeadlineSeconds = 1
        };
        using var handler = new Protocol(options, "pending");
        using var http = new HttpClient(handler);
        var report = await Final100kGrafanaProofScenario.RunAsync(options, http, Secrets);
        report.Failures.ShouldContain("deadline_exceeded");
        options = Options();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        report = await Final100kGrafanaProofScenario.RunAsync(options, http, Secrets, cancel.Token);
        report.Failures.ShouldContain("cancelled");
        File.Exists(Path.Combine(options.ReportDirectory, "report.json")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("isolation")]
    [InlineData("namespace")]
    [InlineData("uri")]
    [InlineData("start")]
    [InlineData("shared-secret")]
    [InlineData("registry")]
    [InlineData("existing-output")]
    public async Task Invalid_configuration_is_rejected_before_network(string fault)
    {
        var options = Options();
        var secrets = Secrets;
        if (fault == "isolation")
            options = options with
            {
                IsolatedTarget = false
            };
        if (fault == "registry")
            options = options with
            {
                CompanyLookupUnavailable = false
            };
        if (fault == "namespace")
            options = options with
            {
                Namespace = "unsafe'_%"
            };
        if (fault == "uri")
            options = options with
            {
                Api = new("http://user:password@api.test/")
            };
        if (fault == "start")
            options = options with
            {
                EventStart = DateTimeOffset.UtcNow
            };
        if (fault == "shared-secret")
            secrets = secrets with
            {
                ViewerGrafana = secrets.InternalGrafana
            };
        if (fault == "existing-output")
        {
            Directory.CreateDirectory(options.ReportDirectory);
            File.WriteAllText(Path.Combine(options.ReportDirectory, "keep.txt"), "preserve");
        }
        using var handler = new Protocol(options);
        using var http = new HttpClient(handler);
        await Should.ThrowAsync<ArgumentException>(() => Final100kGrafanaProofScenario.RunAsync(options, http, secrets));
        handler.Calls.ShouldBe(0);
    }

    private sealed class Protocol(FinalProofOptions o, string fault = "") : HttpMessageHandler
    {
        public int Posts
        {
            get; private set;
        }
        public int SqlPages
        {
            get; private set;
        }
        public int Calls
        {
            get; private set;
        }
        private readonly Dictionary<Guid, int> batches = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var viewer = request.Headers.TryGetValues("X-Grafana-Org-Id", out var orgs) && orgs.Single() == "2";
            var expectedSecret = uri.Host == "api.test" ? (path == "/health/ready" ? null : Secrets.Producer) : uri.Host == "influx.test" ? Secrets.Influx : viewer ? Secrets.ViewerGrafana : Secrets.InternalGrafana;
            request.Headers.Authorization?.Parameter.ShouldBe(expectedSecret);
            if (path == "/health/ready")
                return Reply(new
                {
                    status = "healthy"
                }, fault == "not-ready" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            if (path == "/api/health")
                return Reply(new
                {
                    database = "ok",
                    version = "fixture"
                });
            if (path == "/api/org")
                return Reply(new
                {
                    id = fault == "org" ? 99 : viewer ? 2 : 1
                });
            if (path == "/api/datasources")
                return Reply(new[] { new { type = viewer && fault != "viewer-influx" ? "yesoreyeram-infinity-datasource" : "influxdb" } });
            if (path == "/api/v1/telemetry/batches")
            {
                request.Headers.GetValues("X-Contract-Version").Single().ShouldBe("v1");
                request.Content!.Headers.ContentEncoding.Single().ShouldBe("gzip");
                request.Content.Headers.ContentType!.ToString().ShouldBe("text/plain; charset=utf-8");
                var body = await request.Content.ReadAsByteArrayAsync(ct);
                body.ShouldBe(Final100kGrafanaProofScenario.CreateBatch(o.Namespace, o.EventStart, Posts));
                var id = fault == "duplicate-receipt" && Posts > 0 ? batches.Keys.First() : Guid.NewGuid();
                batches.TryAdd(id, Posts++);
                var statusUrl = "/api/v1/telemetry/batches/" + id;
                var reply = Reply(new
                {
                    batchId = id,
                    receivedAt = DateTimeOffset.UtcNow,
                    state = "accepted",
                    statusUrl = fault == "receipt" ? "http://evil.test/" : statusUrl
                }, HttpStatusCode.Accepted);
                reply.Headers.Location = fault == "location" ? new Uri("http://evil.test/") : new Uri(statusUrl, UriKind.Relative);
                if (fault == "non202")
                    reply.StatusCode = HttpStatusCode.ServiceUnavailable;
                return reply;
            }
            if (path.StartsWith("/api/v1/telemetry/batches/", StringComparison.Ordinal))
                return Reply(new
                {
                    batchId = fault == "status-id" ? Guid.Empty : Guid.Parse(path.Split('/').Last()),
                    state = fault == "terminal" ? "expired" : fault == "pending" ? "processing" : "completed",
                    acceptedLines = 500,
                    rejectedLines = fault == "rejected" ? 1 : 0,
                    replayedLines = fault == "replayed" ? 1 : 0,
                    totalLines = 500
                });
            if (path == "/api/v3/query_sql")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var sql = json.RootElement.GetProperty("q").GetString()!;
                sql.ShouldContain("empresa_id LIKE 'fp-" + o.Namespace + "-%'");
                if (sql.Contains("count(*)", StringComparison.Ordinal))
                    return Reply(new[] { new { n = Posts == 0 ? fault == "dirty" ? 1 : 0 : fault == "missing" ? 99999 : fault == "extra" ? 100001 : 100000 } });
                var offset = int.Parse(sql.Split("OFFSET ", StringSplitOptions.None)[1], CultureInfo.InvariantCulture);
                SqlPages++;
                // Independent expected field values, sorted by company, then lexicographic vehicle, then time.
                var rows = Enumerable.Range(offset, 1000).Select(i =>
                {
                    var n = fault == "duplicate-row" && i == 1 ? 0 : i;
                    var company = n / 10000;
                    var vehicle = company + (n % 10000 / 100) * 10;
                    var sample = n % 100;
                    return new
                    {
                        empresa_id = Final100kGrafanaProofScenario.Company(o.Namespace, company),
                        veiculo_id = vehicle == 0 ? "proof-trip" : $"v{vehicle:D4}",
                        time = o.EventStart.AddSeconds(sample * 20),
                        latitude = fault == "fields" ? 90 : 1 + sample / 1000d,
                        longitude = 2 + vehicle / 10000d,
                        velocidade = sample,
                        rpm = 1000 + sample,
                        ignicao = fault == "ignition" ? sample % 2 != 0 : sample % 2 == 0
                    };
                }).ToArray();
                return Reply(rows);
            }
            if (path.StartsWith("/api/dashboards/uid/", StringComparison.Ordinal))
            {
                var uid = path.Split('/').Last();
                var kind = uid.EndsWith("latest-position", StringComparison.Ordinal) ? "latest-vehicle-position" : uid.EndsWith("vehicle-trip", StringComparison.Ordinal) ? "vehicle-trip" : "company-ingestion-health";
                var file = Path.Combine(FinalProofGrafana.Root(), "deploy/grafana/dashboards", viewer ? "viewer" : "internal", kind + ".json");
                var text = (await File.ReadAllTextAsync(file, ct)).Replace("${VIEWER_DATASOURCE_UID}", "viewer-source", StringComparison.Ordinal);
                var model = JsonNode.Parse(text)!;
                if (fault == "template")
                    model["panels"]![0]!["targets"]![0]![viewer ? "url" : "rawSql"] = "unexpected query";
                return Reply(new
                {
                    dashboard = model
                });
            }
            if (path == "/api/ds/query")
            {
                using var query = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var target = query.RootElement.GetProperty("queries")[0];
                var text = target.GetProperty(viewer ? "url" : "rawSql").GetString()!;
                text.ShouldNotContain("${");
                var company = int.Parse(Regex.Match(text, "fp-" + o.Namespace + "-([0-9]{2})").Groups[1].Value, CultureInfo.InvariantCulture);
                if (viewer && company == 1 && fault != "scope")
                    return Frame([], [], 404);
                if (fault == "grafana-error")
                    return Reply(new
                    {
                        results = new
                        {
                            A = new
                            {
                                error = "synthetic failure",
                                frames = Array.Empty<object>()
                            }
                        }
                    });
                if (fault == "grafana-empty")
                    return Frame([], []);
                var health = viewer ? text.Contains("ingestion-health", StringComparison.Ordinal) : text.Contains("date_bin", StringComparison.Ordinal);
                var trip = viewer ? text.Contains("/trips/", StringComparison.Ordinal) : !health && !text.Contains("ROW_NUMBER", StringComparison.Ordinal);
                if (health)
                    return Frame(viewer ? ["empresaId", "time", "accepted", "rejected"] : ["empresaId", "time", "storedPoints"],
                        viewer ? [[Final100kGrafanaProofScenario.Company(o.Namespace, company)], [o.EventStart.ToUnixTimeMilliseconds()], [fault == "health" ? 9999 : 10000], [0]] :
                        [[Final100kGrafanaProofScenario.Company(o.Namespace, company)], [o.EventStart.ToUnixTimeMilliseconds()], [fault == "health" ? 9999 : 10000]]);
                var points = Enumerable.Range(0, 100).Select(i => new
                {
                    vehicle = trip ? 0 : company + i * 10,
                    sample = trip ? fault == "trip" ? 99 - i : i : fault == "latest" ? 98 : 99
                }).ToArray();
                return Frame(["empresaId", "vehicleId", "eventTime", "latitude", "longitude", "speed", "ignition"],
                    [points.Select(_ => (object)Final100kGrafanaProofScenario.Company(o.Namespace, company)).ToArray(),
                     points.Select(p => (object)(p.vehicle == 0 ? "proof-trip" : $"v{p.vehicle:D4}")).ToArray(),
                     points.Select(p => (object)o.EventStart.AddSeconds(p.sample * 20).ToUnixTimeMilliseconds()).ToArray(),
                     points.Select(p => (object)(1 + p.sample / 1000d)).ToArray(), points.Select(p => (object)(2 + p.vehicle / 10000d)).ToArray(),
                     points.Select(p => (object)p.sample).ToArray(), points.Select(p => (object)(p.sample % 2 == 0)).ToArray()]);
            }
            if (path.StartsWith("/render/", StringComparison.Ordinal))
            {
                var bytes = new byte[1200];
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
                if (fault == "png")
                    bytes[0] = 0;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new("image/png");
                return response;
            }
            throw new InvalidOperationException("Unexpected protocol request.");
        }

        private static HttpResponseMessage Frame(string[] fields, object[][] values, int status = 200) =>
            Reply(new
            {
                results = new
                {
                    A = new
                    {
                        frames = new[] { new { schema = new { fields = fields.Select(name => new { name }), meta = new { custom = new { responseCodeFromServer = status } } }, data = new { values } } }
                    }
                }
            });
        private static HttpResponseMessage Reply(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
    }
}
