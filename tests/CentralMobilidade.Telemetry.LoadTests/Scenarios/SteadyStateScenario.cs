using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Contracts.Api.V1;

namespace CentralMobilidade.Telemetry.LoadTests.Scenarios;

public sealed record SteadyStateOptions(Uri Api, Uri Prometheus, string ReportDirectory,
    Dictionary<string, string> Queries, int DurationSeconds = 600, int MaxConcurrentPosts = 16,
    int DrainSeconds = 300);
public sealed record LatencyPercentiles(double P50, double P95, double P99);
public sealed record ResourceSample(DateTimeOffset Utc, Dictionary<string, double> Values);
public sealed record SteadyStateReport(string RunId, bool Passed, bool ReferenceCapacityRun,
    int TargetEventsPerSecond, long ScheduledEvents, long AttemptedEvents, long AcknowledgedEvents,
    long ReconciledEvents, long CompressedBytes, long DecompressedBytes, double SendSeconds,
    double AcknowledgedEventsPerSecond, double CompressedBytesPerSecond, double DecompressedBytesPerSecond,
    LatencyPercentiles? IntakeMilliseconds, LatencyPercentiles? TerminalObservationMilliseconds,
    double? DiskGrowthBytes, IReadOnlyList<ResourceSample> Samples, IReadOnlyList<string> Failures)
{
    public DateTimeOffset StartedAt
    {
        get; init;
    }
    public int DurationSeconds
    {
        get; init;
    }
    public int MaxConcurrentPosts
    {
        get; init;
    }
    public int DrainSeconds
    {
        get; init;
    }
    public string ConfigurationSha256 { get; init; } = "";
    public IReadOnlyList<Guid> AcceptedBatchIds { get; init; } = [];
    public ReconnectionBurstEvidence? Reconnection
    {
        get; init;
    }
}

/// <summary>Open-loop 500-event batches every 100ms; never retries an ambiguous POST or silently catches up.</summary>
public static class SteadyStateScenario
{
    public static readonly IReadOnlyList<string> RequiredMetrics = Array.AsReadOnly(new[]
    {
        "referenceCpuCores", "referenceMemoryBytes", "cpuPercent", "memoryBytes", "diskUsedBytes",
        "queueMessages", "queueOldestAgeSeconds", "writeP50Seconds", "writeP95Seconds", "writeP99Seconds"
    });
    private const int BatchSize = 500;
    private const long GiB = 1024L * 1024 * 1024;
    private sealed record Accepted(Guid Id, double StartedMilliseconds, int Events, int BurstEvents);

    public static async Task<SteadyStateReport> RunAsync(SteadyStateOptions options, HttpClient http, string producerToken, CancellationToken token = default)
        => await RunCoreAsync(options, http, producerToken, null, token);

    internal static async Task<SteadyStateReport> RunCoreAsync(SteadyStateOptions options, HttpClient http, string producerToken, BurstProfile? burst, CancellationToken token)
    {
        Validate(options, producerToken);
        var run = Guid.NewGuid().ToString("N");
        var start = DateTimeOffset.UtcNow;
        var failures = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var accepted = new ConcurrentDictionary<Guid, Accepted>();
        var intake = new ConcurrentBag<double>();
        var terminal = new ConcurrentBag<double>();
        var observed = new ConcurrentDictionary<Guid, byte>();
        var samples = new List<ResourceSample>();
        long attempted = 0, acknowledged = 0, reconciled = 0, compressed = 0, decompressed = 0;
        long burstAcknowledged = 0, burstReconciled = 0;
        double lastBurstObservation = 0;
        var scheduled = options.DurationSeconds * 5000L + (burst?.Events ?? 0);
        var posts = new List<Task>();
        using var slots = new SemaphoreSlim(options.MaxConcurrentPosts);
        var clock = Stopwatch.StartNew();
        void Fail(string code) => failures.TryAdd(code, 0);

        // Fail before traffic if the reference profile or monitoring is unavailable.
        samples.Add(await SampleAsync(options, http, token));
        CheckSample(samples[0], Fail);
        if (failures.Count != 0)
            throw new InvalidOperationException("Reference profile or resource gate failed before load.");
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var monitor = MonitorAsync();
        var sendClock = Stopwatch.StartNew();
        using var recoveryStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var recovery = burst is null ? Task.CompletedTask : ObserveRecoveryAsync();
        try
        {
            for (var batch = 0; batch < options.DurationSeconds * 10; batch++)
            {
                token.ThrowIfCancellationRequested();
                var due = TimeSpan.FromMilliseconds(batch * 100);
                var remaining = due - sendClock.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, token);
                if (sendClock.Elapsed - due >= TimeSpan.FromMilliseconds(100))
                {
                    Fail("generator_missed_slot");
                    continue;
                }
                if (!slots.Wait(0))
                {
                    Fail("generator_concurrency_saturated");
                    continue;
                }
                posts.RemoveAll(task => task.IsCompleted);
                posts.Add(SendAsync(batch));
            }
            var finalDelay = TimeSpan.FromSeconds(options.DurationSeconds) - sendClock.Elapsed;
            if (finalDelay > TimeSpan.Zero)
                await Task.Delay(finalDelay, token);
            await Task.WhenAll(posts);
            sendClock.Stop();
            // Acknowledgements are not persistence proof; reconcile every accepted batch.
            await recovery;
            var pending = accepted.Where(item => !observed.ContainsKey(item.Key)).ToDictionary(item => item.Key, item => item.Value);
            var drain = Stopwatch.StartNew();
            while (pending.Count > 0 && drain.Elapsed < TimeSpan.FromSeconds(options.DrainSeconds))
            {
                foreach (var group in pending.Values.Chunk(16).ToArray())
                {
                    var outcomes = await Task.WhenAll(group.Select(item => CheckBatchAsync(item)));
                    foreach (var outcome in outcomes)
                        if (outcome.Done)
                        {
                            pending.Remove(outcome.Batch.Id);
                            if (outcome.Valid)
                            {
                                Interlocked.Add(ref reconciled, outcome.Batch.Events);
                                terminal.Add(clock.Elapsed.TotalMilliseconds - outcome.Batch.StartedMilliseconds);
                            }
                        }
                    if (drain.Elapsed >= TimeSpan.FromSeconds(options.DrainSeconds))
                        break;
                }
                if (pending.Count > 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            if (pending.Count != 0)
                Fail("nonterminal_batches");
        }
        catch (OperationCanceledException) { Fail("cancelled"); }
        catch { Fail("scenario_unconfirmed"); }
        finally
        {
            // Stop writes and await ownership before returning or disposing the shared client.
            await Task.WhenAll(posts);
            recoveryStop.Cancel();
            await recovery;
            monitorStop.Cancel();
            await monitor;
        }
        if (!token.IsCancellationRequested)
        {
            try
            {
                var final = await SampleAsync(options, http, token);
                samples.Add(final);
                CheckSample(final, Fail);
            }
            catch { Fail("monitoring_unavailable"); }
        }
        if (acknowledged != scheduled)
            Fail("target_not_acknowledged");
        if (reconciled != acknowledged)
            Fail("acknowledged_not_reconciled");
        if (samples.Count < 2)
            Fail("insufficient_monitoring");
        var seconds = Math.Max(sendClock.Elapsed.TotalSeconds, 0.001);
        if (options.DurationSeconds >= 600 && acknowledged / seconds < scheduled / (double)options.DurationSeconds * 0.99)
            Fail("sustained_rate_not_met");
        if (burst is not null && (burstAcknowledged != burst.Events || burstReconciled != burst.Events))
            Fail("burst_not_drained_by_deadline");
        var report = new SteadyStateReport(run, failures.IsEmpty, burst?.Reference ?? options.DurationSeconds >= 600,
            5000, scheduled, attempted, acknowledged, reconciled, compressed, decompressed,
            seconds, acknowledged / seconds, compressed / seconds, decompressed / seconds,
            Percentiles(intake), Percentiles(terminal), samples.Count >= 2 ? samples[^1].Values["diskUsedBytes"] - samples[0].Values["diskUsedBytes"] : null,
            samples, failures.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray())
        {
            StartedAt = start,
            DurationSeconds = options.DurationSeconds,
            MaxConcurrentPosts = options.MaxConcurrentPosts,
            DrainSeconds = options.DrainSeconds,
            ConfigurationSha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(options))),
            AcceptedBatchIds = accepted.Keys.Order().ToArray(),
            Reconnection = burst is null ? null : new(burst.Vehicles, burst.SamplesPerVehicle, burst.BurstSeconds,
                burst.RecoverySeconds, burst.Events, burstAcknowledged, burstReconciled,
                burstReconciled == burst.Events ? Math.Max(0, lastBurstObservation - burst.BurstSeconds) : null)
        };
        Directory.CreateDirectory(options.ReportDirectory);
        await File.WriteAllTextAsync(Path.Combine(options.ReportDirectory, $"{(burst is null ? "steady" : "reconnect")}-{run}.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        return report;

        async Task SendAsync(int batch)
        {
            var began = clock.Elapsed.TotalMilliseconds;
            try
            {
                var burstEvents = burst?.EventsInBatch(batch) ?? 0;
                var payload = burst is null ? CreateBatch(run, batch, start) : ReconnectionBurstScenario.CreateBatch(run, batch, start, burst);
                Interlocked.Add(ref attempted, BatchSize + burstEvents);
                Interlocked.Add(ref compressed, payload.Gzip.Length);
                Interlocked.Add(ref decompressed, payload.RawBytes);
                using var request = Request(HttpMethod.Post, new Uri(options.Api, "/api/v1/telemetry/batches"));
                request.Headers.Add("X-Contract-Version", "v1");
                request.Content = new ByteArrayContent(payload.Gzip);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain; charset=utf-8");
                request.Content.Headers.ContentEncoding.Add("gzip");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                intake.Add(clock.Elapsed.TotalMilliseconds - began);
                if (response.StatusCode != HttpStatusCode.Accepted)
                {
                    Fail("intake_not_202");
                    return;
                }
                var receipt = JsonSerializer.Deserialize<BatchAccepted>(await ReadBoundedAsync(response, token));
                if (receipt is null || receipt.BatchId == Guid.Empty)
                {
                    Fail("invalid_receipt");
                    return;
                }
                if (!accepted.TryAdd(receipt.BatchId, new(receipt.BatchId, began, BatchSize + burstEvents, burstEvents)))
                {
                    Fail("duplicate_receipt");
                    return;
                }
                Interlocked.Add(ref acknowledged, BatchSize + burstEvents);
                Interlocked.Add(ref burstAcknowledged, burstEvents);
            }
            catch { Fail(token.IsCancellationRequested ? "cancelled" : "intake_unconfirmed"); }
            finally { slots.Release(); }
        }
        HttpRequestMessage Request(HttpMethod method, Uri uri)
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", producerToken);
            return request;
        }
        async Task<(Accepted Batch, bool Done, bool Valid)> CheckBatchAsync(Accepted batch, CancellationToken? observationToken = null)
        {
            var ct = observationToken ?? token;
            try
            {
                using var request = Request(HttpMethod.Get, new Uri(options.Api, $"/api/v1/telemetry/batches/{batch.Id:D}"));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var status = JsonSerializer.Deserialize<BatchStatus>(await ReadBoundedAsync(response, ct));
                if (status is null || status.BatchId != batch.Id)
                {
                    Fail("status_mismatch");
                    return (batch, true, false);
                }
                if (status.State is BatchState.Accepted or BatchState.Processing or BatchState.Retrying)
                    return (batch, false, false);
                var valid = status.State == BatchState.Completed && status.AcceptedLines == batch.Events
                    && status.TotalLines == batch.Events && status.RejectedLines == 0 && status.ReplayedLines == 0;
                if (!valid)
                    Fail("unexpected_terminal_outcome");
                return (batch, true, valid);
            }
            catch { Fail("status_unavailable"); return (batch, false, false); }
        }
        async Task ObserveRecoveryAsync()
        {
            var profile = burst!;
            var deadline = TimeSpan.FromSeconds(profile.BurstSeconds + profile.RecoverySeconds);
            try
            {
                var wait = TimeSpan.FromSeconds(profile.BurstSeconds) - sendClock.Elapsed;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, recoveryStop.Token);
                using var deadlineStop = CancellationTokenSource.CreateLinkedTokenSource(recoveryStop.Token);
                deadlineStop.CancelAfter(deadline - sendClock.Elapsed > TimeSpan.Zero ? deadline - sendClock.Elapsed : TimeSpan.Zero);
                while (sendClock.Elapsed < deadline)
                {
                    foreach (var group in accepted.Values.Where(item => item.BurstEvents > 0 && !observed.ContainsKey(item.Id)).Chunk(16))
                    {
                        deadlineStop.Token.ThrowIfCancellationRequested();
                        // Bound the whole observation, not only individual HTTP requests.
                        var outcomes = await Task.WhenAll(group.Select(item => CheckBatchAsync(item, deadlineStop.Token)));
                        foreach (var outcome in outcomes)
                            if (outcome.Done && observed.TryAdd(outcome.Batch.Id, 0) && outcome.Valid)
                            {
                                Interlocked.Add(ref reconciled, outcome.Batch.Events);
                                terminal.Add(clock.Elapsed.TotalMilliseconds - outcome.Batch.StartedMilliseconds);
                                if (sendClock.Elapsed <= deadline)
                                {
                                    Interlocked.Add(ref burstReconciled, outcome.Batch.BurstEvents);
                                    lastBurstObservation = sendClock.Elapsed.TotalSeconds;
                                }
                            }
                    }
                    if (Interlocked.Read(ref burstReconciled) == profile.Events)
                        return;
                    await Task.Delay(TimeSpan.FromMilliseconds(100), deadlineStop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch { Fail("burst_observation_unavailable"); }
        }
        async Task MonitorAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), monitorStop.Token);
                    var sample = await SampleAsync(options, http, monitorStop.Token);
                    samples.Add(sample);
                    CheckSample(sample, Fail);
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
            catch { Fail("monitoring_unavailable"); }
        }
    }

    public static void Validate(SteadyStateOptions options, string token)
    {
        static bool Origin(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https"
            && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
        if (!Origin(options.Api) || !Origin(options.Prometheus) || string.IsNullOrWhiteSpace(token)
            || token.Any(char.IsWhiteSpace) || options.DurationSeconds is < 1 or > 3600
            || options.MaxConcurrentPosts is < 1 or > 64 || options.DrainSeconds is < 1 or > 300
            || string.IsNullOrWhiteSpace(options.ReportDirectory) || !Path.IsPathFullyQualified(options.ReportDirectory)
            || RequiredMetrics.Any(key => !options.Queries.TryGetValue(key, out var query) || string.IsNullOrWhiteSpace(query)))
            throw new ArgumentException("A bounded scenario, private report directory and complete monitoring configuration are required.");
    }

    public static (byte[] Gzip, long RawBytes) CreateBatch(string run, int batch, DateTimeOffset start)
    {
        var lines = new StringBuilder();
        for (var point = 0; point < BatchSize; point++)
        {
            var ordinal = batch * (long)BatchSize + point;
            var vehicle = ordinal % 100000;
            var timestamp = start.ToUnixTimeMilliseconds() + ordinal / 100000 * 20000;
            lines.Append(CultureInfo.InvariantCulture, $"telemetria,empresa_id=load-{run}-{vehicle % 100},veiculo_id=v{vehicle} latitude=1,longitude=2,velocidade=3,rpm=4i,ignicao=true {timestamp}\n");
        }
        var raw = Encoding.UTF8.GetBytes(lines.ToString());
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true))
            gzip.Write(raw);
        return (buffer.ToArray(), raw.Length);
    }

    public static LatencyPercentiles? Percentiles(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
            return null;
        double P(double quantile) => ordered[(int)Math.Ceiling(quantile * ordered.Length) - 1];
        return new(P(0.50), P(0.95), P(0.99));
    }

    private static void CheckSample(ResourceSample sample, Action<string> fail)
    {
        if (sample.Values["referenceCpuCores"] != 8 || sample.Values["referenceMemoryBytes"] != 16 * GiB)
            fail("reference_profile_mismatch");
        if (sample.Values["cpuPercent"] > 85)
            fail("cpu_limit_exceeded");
        if (sample.Values["memoryBytes"] > 12 * GiB)
            fail("application_memory_limit_exceeded");
    }

    private static async Task<ResourceSample> SampleAsync(SteadyStateOptions options, HttpClient http, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var entries = await Task.WhenAll(RequiredMetrics.Select(async key =>
        {
            using var response = await http.GetAsync(new Uri(options.Prometheus, "/api/v1/query?query=" + Uri.EscapeDataString(options.Queries[key])), HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await ReadBoundedAsync(response, token));
            var root = json.RootElement;
            if (root.TryGetProperty("warnings", out var warnings) && warnings.GetArrayLength() != 0)
                throw new InvalidDataException();
            if (root.GetProperty("status").GetString() != "success" || root.GetProperty("data").GetProperty("resultType").GetString() != "vector")
                throw new InvalidDataException();
            var result = root.GetProperty("data").GetProperty("result");
            if (result.GetArrayLength() != 1)
                throw new InvalidDataException();
            var value = result[0].GetProperty("value");
            var timestamp = value[0].GetDouble();
            var number = double.Parse(value[1].GetString()!, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number) || number < 0 || Math.Abs(now.ToUnixTimeMilliseconds() / 1000d - timestamp) > 45)
                throw new InvalidDataException();
            return new KeyValuePair<string, double>(key, number);
        }));
        return new(now, entries.ToDictionary());
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var source = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await source.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + read > 65536)
                throw new InvalidDataException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
