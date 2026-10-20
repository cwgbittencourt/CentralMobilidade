using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CentralMobilidade.Telemetry.LoadTests.Scenarios;

public sealed record FinalProofOptions(Uri Api, Uri Influx, Uri Grafana, string ReportDirectory,
    string Namespace, DateTimeOffset EventStart, bool IsolatedTarget, int DeadlineSeconds = 1800,
    int PollMilliseconds = 500, int InternalOrganizationId = 1, int ViewerOrganizationId = 2,
    bool CompanyLookupUnavailable = false);
public sealed record FinalProofSecrets(string Producer, string Influx, string InternalGrafana, string ViewerGrafana)
{
    public override string ToString() => "[FinalProofSecrets]";
}
public sealed record ProofEvent(string Company, string Vehicle, DateTimeOffset Time, double Latitude,
    double Longitude, double Speed, int Rpm, bool Ignition);
public sealed record FinalProofReport(bool Passed, string Stage, long AcceptedEvents, long StoredEvents,
    IReadOnlyList<Guid> BatchIds, IReadOnlyList<string> Failures)
{
    public DateTimeOffset StartedAt
    {
        get; init;
    }
    public DateTimeOffset FinishedAt
    {
        get; init;
    }
    public string CorpusSha256 { get; init; } = "";
    public string ConfigurationSha256 { get; init; } = "";
    public bool CapacityApproved => false;
}

public static class Final100kGrafanaProofScenario
{
    public const int EventCount = 100000;
    public const int VehicleCount = 1000;
    public const string TripVehicle = "proof-trip";
    public static string Company(string run, int company) => $"fp-{run}-{company:D2}";
    internal static ProofEvent Event(string run, DateTimeOffset start, int vehicle, int sample) =>
        new(Company(run, vehicle % 10), vehicle == 0 ? TripVehicle : $"v{vehicle:D4}", start.AddSeconds(sample * 20),
            1 + sample / 1000d, 2 + vehicle / 10000d, sample, 1000 + sample, sample % 2 == 0);

    public static IEnumerable<ProofEvent> Events(string run, DateTimeOffset start)
    {
        for (var arrival = 0; arrival < 100; arrival++)
            for (var vehicle = 0; vehicle < VehicleCount; vehicle++)
                yield return Event(run, start, vehicle, (arrival + 50) % 100);
    }

    internal static string Line(ProofEvent e) => FormattableString.Invariant(
        $"telemetria,empresa_id={e.Company},veiculo_id={e.Vehicle} latitude={e.Latitude:R},longitude={e.Longitude:R},velocidade={e.Speed:R},rpm={e.Rpm}i,ignicao={(e.Ignition ? "true" : "false")} {e.Time.ToUnixTimeMilliseconds()}\n");

    public static byte[] CreateBatch(string run, DateTimeOffset start, int batch)
    {
        if (batch is < 0 or >= 200)
            throw new ArgumentOutOfRangeException(nameof(batch));
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
            for (var i = batch * 500; i < (batch + 1) * 500; i++)
                gzip.Write(Encoding.UTF8.GetBytes(Line(Event(run, start, i % 1000, (i / 1000 + 50) % 100))));
        return output.ToArray();
    }

    internal static void Validate(FinalProofOptions o, FinalProofSecrets s)
    {
        foreach (var uri in new[] { o.Api, o.Influx, o.Grafana })
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Use credential-free HTTP(S) origins.");
        if (!o.IsolatedTarget || !o.CompanyLookupUnavailable || !Regex.IsMatch(o.Namespace ?? "", "\\A[a-f0-9]{32}\\z", RegexOptions.CultureInvariant) ||
            !Path.IsPathFullyQualified(o.ReportDirectory) || o.DeadlineSeconds is < 1 or > 3600 ||
            o.PollMilliseconds is < 1 or > 5000 || o.InternalOrganizationId != 1 || o.ViewerOrganizationId <= 1 ||
            o.EventStart.Offset != TimeSpan.Zero || o.EventStart.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            o.EventStart < DateTimeOffset.UtcNow.AddHours(-12) || o.EventStart.AddSeconds(2000) >= DateTimeOffset.UtcNow ||
            new[] { s.Producer, s.Influx, s.InternalGrafana, s.ViewerGrafana }.Any(string.IsNullOrWhiteSpace) ||
            new[] { s.Producer, s.Influx, s.InternalGrafana, s.ViewerGrafana }.Distinct(StringComparer.Ordinal).Count() != 4)
            throw new ArgumentException("Invalid isolated final-proof configuration or distinct credentials.");
        if (Directory.Exists(o.ReportDirectory) && Directory.EnumerateFileSystemEntries(o.ReportDirectory).Any())
            throw new ArgumentException("Evidence directory must be new or empty.");
    }

    public static async Task<FinalProofReport> RunAsync(FinalProofOptions options, HttpClient http, FinalProofSecrets secrets,
        CancellationToken token = default)
    {
        Validate(options, secrets);
        Directory.CreateDirectory(options.ReportDirectory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.DeadlineSeconds));
        var ct = deadline.Token;
        var started = DateTimeOffset.UtcNow;
        var stage = "preflight";
        long accepted = 0, stored = 0;
        var ids = new List<Guid>();
        var failures = new List<string>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string digest = "";
        var wire = new ProofHttp(http, options, secrets);
        try
        {
            await wire.Save("manifest.json", new
            {
                GeneratorVersion = 1,
                Options = options,
                Events = EventCount,
                Vehicles = VehicleCount,
                Companies = 10,
                SamplesPerVehicle = 100,
                BatchSize = 500,
                Batches = 200,
                TripVehicle,
                ArrivalOrder = "samples 50..99 then 0..49; each sample visits vehicles 0..999",
                started
            }, ct);
            using (var ready = await wire.Send(options.Api, "health/ready", null, ct))
                ProofHttp.Require(ready.StatusCode == HttpStatusCode.OK, "api_not_ready");
            ProofHttp.Require(await CountAsync() == 0, "namespace_not_empty");
            await FinalProofGrafana.PreflightAsync(wire, options, secrets, ct);
            stage = "intake";
            for (var batch = 0; batch < 200; batch++)
            {
                for (var i = batch * 500; i < (batch + 1) * 500; i++)
                    hash.AppendData(Encoding.UTF8.GetBytes(Line(Event(options.Namespace, options.EventStart, i % 1000, (i / 1000 + 50) % 100))));
                using var content = new ByteArrayContent(CreateBatch(options.Namespace, options.EventStart, batch));
                content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
                content.Headers.ContentEncoding.Add("gzip");
                using var response = await wire.Send(options.Api, "api/v1/telemetry/batches", secrets.Producer, ct, content, contract: true);
                ProofHttp.Require(response.StatusCode == HttpStatusCode.Accepted, "intake_not_202");
                var receipt = await ProofHttp.Json(response, ct);
                var id = receipt.GetProperty("batchId").GetGuid();
                var path = "/api/v1/telemetry/batches/" + id;
                ProofHttp.Require(id != Guid.Empty && !ids.Contains(id) && receipt.GetProperty("state").GetString() == "accepted" &&
                    receipt.GetProperty("statusUrl").GetString() == path && response.Headers.Location?.OriginalString == path &&
                    receipt.GetProperty("receivedAt").GetDateTimeOffset().Offset == TimeSpan.Zero, "invalid_receipt");
                ids.Add(id);
                accepted += 500;
            }
            digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            stage = "terminal_batches";
            foreach (var id in ids)
            {
                while (true)
                {
                    using var response = await wire.Send(options.Api, "api/v1/telemetry/batches/" + id, secrets.Producer, ct);
                    var status = await ProofHttp.Json(response, ct);
                    ProofHttp.Require(status.GetProperty("batchId").GetGuid() == id, "status_identity_mismatch");
                    var state = status.GetProperty("state").GetString();
                    if (state == "completed")
                    {
                        ProofHttp.Require(status.GetProperty("acceptedLines").GetInt64() == 500 && status.GetProperty("rejectedLines").GetInt64() == 0 &&
                            status.GetProperty("replayedLines").GetInt64() == 0 && status.GetProperty("totalLines").GetInt64() == 500, "terminal_counters_mismatch");
                        await wire.Save("batch-" + id + ".json", status, ct);
                        break;
                    }
                    ProofHttp.Require(state is "accepted" or "processing" or "retrying", "batch_not_successful");
                    await Task.Delay(options.PollMilliseconds, ct);
                }
            }
            stage = "independent_influx";
            stored = await CountAsync();
            ProofHttp.Require(stored == EventCount, "stored_count_mismatch");
            var seen = new HashSet<(int Vehicle, int Sample)>();
            for (var offset = 0; offset < EventCount; offset += 1000)
            {
                var rows = await wire.Sql($"SELECT empresa_id,veiculo_id,time,latitude,longitude,velocidade,rpm,ignicao FROM telemetria WHERE {wire.NamespaceWhere} ORDER BY empresa_id,veiculo_id,time LIMIT 1000 OFFSET {offset}", ct);
                ProofHttp.Require(rows.GetArrayLength() == 1000, "stored_page_incomplete");
                foreach (var row in rows.EnumerateArray())
                {
                    var name = row.GetProperty("veiculo_id").GetString()!;
                    var vehicle = name == TripVehicle ? 0 : int.Parse(name.AsSpan(1), CultureInfo.InvariantCulture);
                    var time = ProofHttp.UtcTimestamp(row.GetProperty("time"));
                    var delta = time.ToUnixTimeMilliseconds() - options.EventStart.ToUnixTimeMilliseconds();
                    var sample = checked((int)(delta / 20000));
                    ProofHttp.Require(vehicle is >= 0 and < 1000 && sample is >= 0 and < 100 && delta % 20000 == 0 && seen.Add((vehicle, sample)), "stored_identity_mismatch");
                    var expected = Event(options.Namespace, options.EventStart, vehicle, sample);
                    ProofHttp.Require(name == expected.Vehicle && row.GetProperty("empresa_id").GetString() == expected.Company && time == expected.Time &&
                        Math.Abs(row.GetProperty("latitude").GetDouble() - expected.Latitude) < 1e-9 &&
                        Math.Abs(row.GetProperty("longitude").GetDouble() - expected.Longitude) < 1e-9 &&
                        row.GetProperty("velocidade").GetDouble() == expected.Speed && row.GetProperty("rpm").GetInt32() == expected.Rpm &&
                        row.GetProperty("ignicao").GetBoolean() == expected.Ignition, "stored_fields_mismatch");
                }
                await wire.Save($"influx-page-{offset:D6}.json", rows, ct);
            }
            ProofHttp.Require(seen.Count == EventCount, "stored_identity_mismatch");
            ProofHttp.Require(await CountAsync() == EventCount, "stored_count_mismatch");
            stage = "grafana";
            await FinalProofGrafana.VerifyAsync(wire, options, secrets, started, ct);
            stage = "complete";
        }
        catch (ProofFailure error) { failures.Add(error.Code); }
        catch (OperationCanceledException) { failures.Add(token.IsCancellationRequested ? "cancelled" : "deadline_exceeded"); }
        catch { failures.Add("proof_step_failed"); } // Never serialize provider diagnostics or secrets.
        var report = new FinalProofReport(failures.Count == 0, stage, accepted, stored, ids, failures)
        {
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            CorpusSha256 = digest,
            ConfigurationSha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(options)))
        };
        await wire.Save("report.json", report, CancellationToken.None);
        return report;

        async Task<long> CountAsync()
        {
            var result = await wire.Sql($"SELECT count(*) AS n FROM telemetria WHERE {wire.NamespaceWhere}", ct);
            await wire.Save(stage == "preflight" ? "influx-before.json" : "influx-count.json", result, ct);
            return result[0].GetProperty("n").GetInt64();
        }
    }
}

internal sealed class ProofFailure(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal sealed class ProofHttp(HttpClient http, FinalProofOptions options, FinalProofSecrets secrets)
{
    internal string NamespaceWhere => $"empresa_id LIKE 'fp-{options.Namespace}-%'";
    internal static void Require(bool success, string code)
    {
        if (!success)
            throw new ProofFailure(code);
    }
    internal async Task<HttpResponseMessage> Send(Uri origin, string path, string? secret, CancellationToken ct,
        HttpContent? content = null, int? org = null, bool contract = false)
    {
        using var request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, new Uri(origin, path));
        request.Content = content;
        if (secret is not null)
            request.Headers.Authorization = new("Bearer", secret);
        if (org is not null)
            request.Headers.Add("X-Grafana-Org-Id", org.Value.ToString(CultureInfo.InvariantCulture));
        if (contract)
            request.Headers.Add("X-Contract-Version", "v1");
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    internal static async Task<byte[]> Bytes(HttpResponseMessage response, CancellationToken ct, bool deniedQuery = false)
    {
        Require(response.IsSuccessStatusCode || deniedQuery && response.StatusCode == HttpStatusCode.BadRequest, "http_failure");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            Require(output.Length + count <= 2 * 1024 * 1024, "response_limit_exceeded");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    internal static async Task<JsonElement> Json(HttpResponseMessage response, CancellationToken ct, bool deniedQuery = false)
    {
        using var json = JsonDocument.Parse(await Bytes(response, ct, deniedQuery));
        return json.RootElement.Clone();
    }
    internal async Task<JsonElement> Sql(string query, CancellationToken ct)
    {
        using var body = new StringContent(JsonSerializer.Serialize(new
        {
            db = "telemetry",
            q = query,
            format = "json"
        }), Encoding.UTF8, "application/json");
        using var response = await Send(options.Influx, "api/v3/query_sql", secrets.Influx, ct, body);
        return await Json(response, ct);
    }
    internal Task Save(string name, object value, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(options.ReportDirectory, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), ct);

    // Influx SQL serializes its UTC timestamps without an offset. Never assume the runner's local zone.
    internal static DateTimeOffset UtcTimestamp(JsonElement value) =>
        DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
