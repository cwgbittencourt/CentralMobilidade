using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CentralMobilidade.Telemetry.LoadTests.Scenarios;

public sealed record InfluxOutageOptions(SteadyStateOptions Load, Uri FaultControl, string ProxyName,
    string ExpectedListen, string ExpectedUpstream, bool IsolatedTarget = false, int OutageSeconds = 120, int RecoverySeconds = 600);
public sealed record SevenDayBacklogProjection(long Events, long Messages, double GzipPayloadBytes,
    double? QueueDiskBytes, double? ObservedRecoveryEventsPerSecondLowerBound,
    double? CatchUpSecondsWithNormalTraffic, string Assessment);
public sealed record InfluxOutageReport(string RunId, bool Passed, bool ReferenceCapacityRun,
    bool ProxyRestored, double? ObservedOutageSeconds, double? ObservedRecoverySeconds,
    ResourceSample Before, ResourceSample? During, ResourceSample? After,
    SteadyStateReport? Load, SevenDayBacklogProjection? SevenDays, IReadOnlyList<string> Failures,
    string ConfigurationSha256);

/// <summary>Disables only a pre-existing isolated Influx proxy; always attempts to restore its original enabled state.</summary>
public static class InfluxOutageScenario
{
    public static readonly IReadOnlyList<string> RequiredMetrics = SteadyStateScenario.RequiredMetrics.Concat(new[]
    { "queuePayloadBytes", "queueDiskUsedBytes", "storageWriteFailures", "storageWriteConfirmations" }).ToArray();

    public static async Task<InfluxOutageReport> RunAsync(InfluxOutageOptions options, HttpClient http, string producerToken, CancellationToken token = default, string? faultToken = null)
    {
        Validate(options, producerToken);
        if (faultToken is not null && (string.IsNullOrWhiteSpace(faultToken) || faultToken.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Invalid fault-control credential.");
        var loadOptions = options.Load with
        {
            DurationSeconds = options.OutageSeconds + options.RecoverySeconds
        };
        var before = await SteadyStateScenario.SampleAsync(loadOptions, http, token, RequiredMetrics);
        SteadyStateScenario.CheckSample(before, _ => throw new InvalidOperationException("Reference profile gate failed."));
        if (before.Values["queueMessages"] != 0 || before.Values["queuePayloadBytes"] != 0)
            throw new InvalidOperationException("Start with an empty isolated queue.");
        var initial = await ReadProxyAsync(token);
        if (!initial.Enabled)
            throw new InvalidOperationException("Proxy must initially be enabled.");
        var failures = new HashSet<string>(StringComparer.Ordinal);
        ResourceSample? during = null, after = null;
        SteadyStateReport? load = null;
        SevenDayBacklogProjection? projection = null;
        Task<SteadyStateReport>? running = null;
        double? observedOutage = null, observedRecovery = null;
        var mutationAttempted = false;
        var restored = false;
        using var loadStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watch = new Stopwatch();
        try
        {
            mutationAttempted = true; // Also restore after an ambiguous disable response.
            await SetProxyAsync(false, token);
            watch.Start();
            running = SteadyStateScenario.RunAsync(loadOptions, http, producerToken, loadStop.Token);
            await Task.Delay(TimeSpan.FromSeconds(options.OutageSeconds), token);
            during = await SteadyStateScenario.SampleAsync(loadOptions, http, token, RequiredMetrics);
            if (during.Values["queueMessages"] <= before.Values["queueMessages"]
                || during.Values["queuePayloadBytes"] <= before.Values["queuePayloadBytes"])
                failures.Add("outage_backlog_not_observed");
            if (during.Values["storageWriteFailures"] <= before.Values["storageWriteFailures"])
                failures.Add("write_failure_not_observed");
            if (during.Values["storageWriteConfirmations"] != before.Values["storageWriteConfirmations"])
                failures.Add("writes_confirmed_during_outage_or_counter_reset");
            await SetProxyAsync(true, token);
            restored = true;
            observedOutage = watch.Elapsed.TotalSeconds;
            var recoveryStart = watch.Elapsed;
            load = await running;
            after = await SteadyStateScenario.SampleAsync(loadOptions, http, token, RequiredMetrics);
            observedRecovery = (watch.Elapsed - recoveryStart).TotalSeconds;
            if (!load.Passed)
                failures.Add("load_or_reconciliation_failed");
            if (after.Values["queueMessages"] > before.Values["queueMessages"]
                || after.Values["queuePayloadBytes"] > before.Values["queuePayloadBytes"])
                failures.Add("queue_not_recovered_to_baseline");
            if (after.Values["storageWriteConfirmations"] <= during.Values["storageWriteConfirmations"])
                failures.Add("write_recovery_not_observed");
            var queueGrowth = during.Values["queueDiskUsedBytes"] - before.Values["queueDiskUsedBytes"];
            // Only a fully reconciled cohort supports a recovery-throughput lower bound.
            projection = ProjectSevenDays(load.AttemptedEvents, load.CompressedBytes,
                during.Values["queueMessages"] - before.Values["queueMessages"], queueGrowth,
                failures.Count == 0 && load.Passed && load.ReconciledEvents == load.AcknowledgedEvents ? load.ReconciledEvents : 0,
                observedRecovery.Value);
        }
        catch (OperationCanceledException) { failures.Add("cancelled"); }
        catch { failures.Add("outage_scenario_unconfirmed"); }
        finally
        {
            if (mutationAttempted && !restored)
            {
                // Cleanup has its own deadline and does not inherit a cancelled load token.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await SetProxyAsync(true, cleanup.Token);
                    restored = true;
                }
                catch { failures.Add("proxy_restore_unconfirmed"); }
            }
            if (running is not null && load is null)
            {
                loadStop.Cancel();
                try
                {
                    load = await running;
                }
                catch { failures.Add("load_result_unavailable"); }
            }
        }
        if (!restored)
            failures.Add("proxy_restore_unconfirmed");
        var report = new InfluxOutageReport(Guid.NewGuid().ToString("N"), failures.Count == 0 && load?.Passed == true,
            options.OutageSeconds >= 120 && options.RecoverySeconds >= 600 && load?.ReferenceCapacityRun == true,
            restored, observedOutage, observedRecovery, before, during, after, load, projection,
            failures.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(options))));
        Directory.CreateDirectory(loadOptions.ReportDirectory);
        await File.WriteAllTextAsync(Path.Combine(loadOptions.ReportDirectory, $"outage-{report.RunId}.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        return report;

        async Task<ProxyState> ReadProxyAsync(CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = FaultRequest(HttpMethod.Get);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await SteadyStateScenario.ReadBoundedAsync(response, deadline.Token));
            var root = json.RootElement;
            if (root.GetProperty("name").GetString() != options.ProxyName
                || root.GetProperty("listen").GetString() != options.ExpectedListen
                || root.GetProperty("upstream").GetString() != options.ExpectedUpstream)
                throw new InvalidDataException();
            return new(root.GetProperty("enabled").GetBoolean());
        }
        async Task SetProxyAsync(bool enabled, CancellationToken ct)
        {
            await ReadProxyAsync(ct); // Refuse a changed identity before every mutation, including cleanup.
            using var request = FaultRequest(HttpMethod.Post);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                enabled
            }), Encoding.UTF8, "application/json");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if ((await ReadProxyAsync(deadline.Token)).Enabled != enabled)
                throw new InvalidDataException();
        }
        HttpRequestMessage FaultRequest(HttpMethod method)
        {
            var request = new HttpRequestMessage(method, new Uri(options.FaultControl, "/proxies/" + options.ProxyName));
            if (faultToken is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", faultToken);
            return request;
        }
    }

    public static void Validate(InfluxOutageOptions options, string producerToken)
    {
        SteadyStateScenario.Validate(options.Load with
        {
            DurationSeconds = options.OutageSeconds + options.RecoverySeconds
        }, producerToken);
        if (!options.IsolatedTarget || options.Load.Vehicles != 100000 || !options.FaultControl.IsAbsoluteUri || !options.FaultControl.IsLoopback
            || options.FaultControl.Scheme is not ("http" or "https") || options.FaultControl.UserInfo.Length != 0
            || options.FaultControl.AbsolutePath != "/" || options.FaultControl.Query.Length != 0 || options.FaultControl.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(options.ProxyName) || options.ProxyName.Length > 64
            || options.ProxyName.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_'))
            || string.IsNullOrWhiteSpace(options.ExpectedListen) || string.IsNullOrWhiteSpace(options.ExpectedUpstream)
            || options.OutageSeconds is < 1 or > 600 || options.RecoverySeconds is < 1 or > 1800
            || RequiredMetrics.Any(key => !options.Load.Queries.TryGetValue(key, out var query) || string.IsNullOrWhiteSpace(query)))
            throw new ArgumentException("An isolated target, exact existing proxy and complete outage metrics are required.");
    }

    public static SevenDayBacklogProjection ProjectSevenDays(long measuredEvents, long gzipBytes, double queuedMessages,
        double queueDiskGrowthBytes, long recoveredEvents, double recoverySeconds)
    {
        if (measuredEvents <= 0 || gzipBytes <= 0 || !double.IsFinite(queuedMessages) || queuedMessages < 0
            || !double.IsFinite(queueDiskGrowthBytes) || recoveredEvents < 0 || !double.IsFinite(recoverySeconds) || recoverySeconds <= 0)
            throw new ArgumentException("Finite observed measurements are required.");
        const long events = 5000L * 7 * 24 * 60 * 60;
        const long messages = events / 500;
        double? throughput = recoveredEvents > 0 ? recoveredEvents / recoverySeconds : null;
        return new(events, messages, gzipBytes / (double)measuredEvents * events,
            queuedMessages > 0 && queueDiskGrowthBytes > 0 ? queueDiskGrowthBytes / queuedMessages * messages : null,
            throughput, throughput > 5000 ? events / (throughput.Value - 5000) : null,
            throughput > 5000 ? "estimate_only" : "catch_up_not_demonstrated");
    }

    private sealed record ProxyState(bool Enabled);
}
