using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;
using Xunit.Sdk;

namespace CentralMobilidade.Telemetry.IntegrationTests.Privacy;

[Collection("Observability")]
public sealed class ObservabilityLeakTests(LokiPrivacyFixture loki) : IClassFixture<LokiPrivacyFixture>
{
    [Theory]
    [InlineData(TelemetryServiceRole.Api)]
    [InlineData(TelemetryServiceRole.Worker)]
    public async Task Production_observability_exports_no_sensitive_canaries(TelemetryServiceRole role)
    {
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collectorBuilder = WebApplication.CreateSlimBuilder();
        collectorBuilder.Logging.ClearProviders();
        await using var collector = collectorBuilder.Build();
        collector.Urls.Add("http://127.0.0.1:0");
        collector.MapPost("/v1/traces", async (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            using var bytes = new MemoryStream();
            await context.Request.Body.CopyToAsync(bytes);
            context.Response.ContentType = "application/x-protobuf";
            received.TrySetResult(bytes.ToArray());
        });
        await collector.StartAsync();

        using var logs = new StringWriter(CultureInfo.InvariantCulture);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Observability:OtlpTraceEndpoint"] = collector.Urls.Single() + "/v1/traces";
        builder.Configuration["Observability:WorkerMetricsUrl"] = "http://127.0.0.1:0";
        builder.Services.AddPrivacyLogging(role, logs);
        builder.Services.AddTelemetryObservability(builder.Configuration, role);
        await using var app = builder.Build();
        app.MapTelemetryMetrics();
        var batch = BatchId.New();
        var payload = string.Join(" | ", PrivacyCanaryScanner.Seeds.Select(seed => seed.Value));
        app.MapPost("/probe/{driver}", (ILoggerFactory factory, TelemetryObservability telemetry) =>
        {
            using var span = telemetry.StartBatchActivity(TelemetryOperation.ProcessBatch, batch);
            span.ShouldNotBeNull();
            foreach (var seed in PrivacyCanaryScanner.Seeds)
            {
                span.SetTag(seed.Key, seed.Value);
                span.AddBaggage(seed.Key, seed.Value);
            }
            span.DisplayName = payload;
            span.SetStatus(ActivityStatusCode.Error, payload);
            var logger = factory.CreateLogger(payload);
            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["BatchId"] = batch.ToString(),
                ["Authorization"] = "Bearer " + PrivacyCanaryScanner.Seeds["token"]
            });
            logger.LogError(new InvalidOperationException(payload),
                "Sensitive input {Payload} {@Driver} {ErrorCode}", payload,
                PrivacyCanaryScanner.Seeds, "STORAGE_REJECTED");
            telemetry.RecordReceived(128);
            return "ok";
        });
        await app.StartAsync();
        var provider = app.Services.GetRequiredService<TracerProvider>();
        using var meter = new Meter(TelemetryObservability.MeterName);
        var counter = meter.CreateCounter<long>("telemetry.privacy.canary");
        var tags = new TagList();
        foreach (var seed in PrivacyCanaryScanner.Seeds)
            tags.Add(seed.Key, seed.Value);
        counter.Add(1, tags);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/probe/" + Uri.EscapeDataString(PrivacyCanaryScanner.Seeds["name"]) + "?payload=" + Uri.EscapeDataString(payload));
        request.Headers.Authorization = new("Bearer", PrivacyCanaryScanner.Seeds["token"]);
        request.Headers.Add("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        request.Headers.Add("baggage", "driver=" + Uri.EscapeDataString(PrivacyCanaryScanner.Seeds["name"]));
        request.Content = new StringContent(payload);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var metrics = role == TelemetryServiceRole.Api
            ? await client.GetStringAsync("/metrics")
            : await ReadWorkerMetrics(app.Services);
        PrivacyCanaryScanner.AssertClean("Prometheus exposition", metrics);
        metrics.Split('\n').Any(line => line.StartsWith("telemetry_privacy_canary", StringComparison.Ordinal)
            && line.TrimEnd().EndsWith(" 1", StringComparison.Ordinal)).ShouldBeTrue();
        metrics.ShouldContain("telemetry_batches_received");
        provider.ForceFlush(5000).ShouldBeTrue();
        var traces = Encoding.UTF8.GetString(await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        traces.ShouldContain("telemetry.process_batch");
        traces.ShouldContain(batch.ToString());
        traces.ShouldContain("centralmobilidade.telemetry." + role.ToString().ToLowerInvariant());
        await app.StopAsync();
        var logText = logs.ToString();
        var lines = logText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldNotBeEmpty();
        logText.ShouldContain(batch.ToString());
        logText.ShouldContain("STORAGE_REJECTED");
        PrivacyCanaryScanner.AssertClean("application logs", logText);
        PrivacyCanaryScanner.AssertClean("Prometheus exposition", metrics);
        PrivacyCanaryScanner.AssertClean("OTLP protobuf strings", traces);

        // Explicit test transport: no claim that a production log shipper is configured.
        var query = await loki.PushAndQuery(lines, role);
        PrivacyCanaryScanner.AssertClean("Loki query JSON", query);
        using var document = JsonDocument.Parse(query);
        var returned = document.RootElement.GetProperty("data").GetProperty("result")
            .EnumerateArray().SelectMany(stream => stream.GetProperty("values").EnumerateArray())
            .Select(value => value[1].GetString()!).ToArray();
        returned.Length.ShouldBe(lines.Length);
        foreach (var line in lines)
            returned.ShouldContain(line.TrimEnd('\r'));
        PrivacyCanaryScanner.AssertClean("Loki decoded lines", string.Join('\n', returned));
        await collector.StopAsync();
    }

    private static async Task<string> ReadWorkerMetrics(IServiceProvider services)
    {
        var endpoint = services.GetServices<IHostedService>().OfType<WorkerMetricsEndpoint>().Single();
        using var client = new HttpClient();
        return await client.GetStringAsync(endpoint.ListenUrl + "/metrics");
    }

    [Fact]
    public async Task Loki_negative_control_preserves_each_deliberate_leak_for_scanner_detection()
    {
        foreach (var seed in PrivacyCanaryScanner.Seeds)
        {
            var query = await loki.PushAndQuery([seed.Value], TelemetryServiceRole.Api);
            var error = Should.Throw<XunitException>(() => PrivacyCanaryScanner.AssertClean("Loki negative control", query));
            error.Message.ShouldContain("category=" + seed.Key);
        }
    }
}

internal static class PrivacyCanaryScanner
{
    // Synthetic values only. Separate coordinate components and CPF formats are intentional.
    internal static readonly IReadOnlyDictionary<string, string> Seeds = new Dictionary<string, string>
    {
        ["plate"] = "QZX9J83",
        ["latitude"] = "-23.918273",
        ["longitude"] = "-46.817263",
        ["name"] = "João Canário T079",
        ["cpf"] = "98765432100",
        ["formatted-cpf"] = "987.654.321-00",
        ["token"] = "cm_p_T079_synthetic_7c29b36e0d514a80"
    };

    internal static IEnumerable<string> Representations(string value)
    {
        yield return value;
        yield return JsonSerializer.Serialize(value)[1..^1];
        yield return Uri.EscapeDataString(value);
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    internal static void AssertClean(string surface, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new XunitException($"Privacy scan received an empty surface: {surface}.");
        foreach (var seed in Seeds)
            if (Representations(seed.Value).Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase)))
                // Never echo the observed text or sensitive value into test/CI diagnostics.
                throw new XunitException($"Privacy canary detected: category={seed.Key}; surface={surface}.");
    }
}

public sealed class PrivacyCanaryScannerTests
{
    public static IEnumerable<object[]> CanaryCases() => PrivacyCanaryScanner.Seeds.SelectMany(seed =>
        PrivacyCanaryScanner.Representations(seed.Value).Select((value, index) => new object[] { seed.Key, index, value }));

    [Theory]
    [MemberData(nameof(CanaryCases))]
    public void Scanner_detects_each_seed_and_encoding_on_every_surface_without_echoing_it(string category, int encoding, string value)
    {
        foreach (var surface in new[] { "application logs", "Loki query JSON", "Prometheus exposition", "OTLP protobuf strings" })
        {
            var error = Should.Throw<XunitException>(() => PrivacyCanaryScanner.AssertClean(surface, "prefix " + value + " suffix"));
            error.Message.ShouldContain("category=" + category);
            error.Message.ShouldContain("surface=" + surface);
            error.Message.ShouldNotContain(value);
        }
        encoding.ShouldBeInRange(0, 3);
    }

    [Fact]
    public void Scanner_rejects_empty_capture_and_accepts_safe_technical_correlation()
    {
        Should.Throw<XunitException>(() => PrivacyCanaryScanner.AssertClean("empty", " "));
        PrivacyCanaryScanner.AssertClean("technical", "batchId=" + Guid.NewGuid() + " STORAGE_REJECTED veiculo_id=vehicle-79");
    }
}

public sealed class LokiPrivacyFixture : IAsyncLifetime
{
    private readonly IContainer container;

    public LokiPrivacyFixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CentralMobilidade.slnx")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Repository root unavailable for Loki bootstrap.");
        container = new ContainerBuilder("grafana/loki:3.7.0")
            .WithCreateParameterModifier(parameters => parameters.User = "0")
            .WithResourceMapping(Path.Combine(root.FullName, "deploy", "loki", "bootstrap.yaml"), "/etc/loki")
            .WithCommand("-config.file=/etc/loki/bootstrap.yaml")
            .WithPortBinding(3100, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3100).ForPath("/ready")))
            .Build();
    }

    public Task InitializeAsync() => container.StartAsync();
    public async Task DisposeAsync() => await container.DisposeAsync();

    internal async Task<string> PushAndQuery(string[] lines, TelemetryServiceRole role)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(3100)}") };
        var run = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        using var push = await client.PostAsJsonAsync("/loki/api/v1/push", new
        {
            streams = new[] { new { stream = new { service = role.ToString().ToLowerInvariant(), privacy_run = run },
                values = lines.Select((line, index) => new[] { (now + index).ToString(CultureInfo.InvariantCulture), line.TrimEnd('\r') }).ToArray() } }
        });
        push.EnsureSuccessStatusCode();
        var query = "/loki/api/v1/query_range?query=" + Uri.EscapeDataString("{privacy_run=\"" + run + "\"}")
            + "&start=" + (now - 60_000_000_000).ToString(CultureInfo.InvariantCulture)
            + "&end=" + (now + 60_000_000_000).ToString(CultureInfo.InvariantCulture) + "&limit=5000";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            var text = await client.GetStringAsync(query);
            using var document = JsonDocument.Parse(text);
            document.RootElement.GetProperty("status").GetString().ShouldBe("success");
            var count = document.RootElement.GetProperty("data").GetProperty("result").EnumerateArray()
                .Sum(stream => stream.GetProperty("values").GetArrayLength());
            if (count == lines.Length)
                return text;
            await Task.Delay(200);
        }
        throw new XunitException("Loki did not return every privacy test event before the deadline.");
    }
}
