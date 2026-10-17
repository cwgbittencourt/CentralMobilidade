using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.IntegrationTests.Batches;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
namespace CentralMobilidade.Telemetry.IntegrationTests.Observability;

[CollectionDefinition("BatchMetricsIsolation", DisableParallelization = true)]
public sealed class BatchMetricsIsolationCollection
{
}

[Collection("BatchMetricsIsolation")]
public sealed class BatchMetricsPersistenceTests(BatchLifecycleDatabase database) : IClassFixture<BatchLifecycleDatabase>
{
    [Fact]
    public async Task Prometheus_counts_commits_once_and_preserves_history_after_metadata_cleanup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Api);
        builder.Services.AddTelemetryControlPersistence(database.Connection);
        await using var app = builder.Build();
        app.MapTelemetryMetrics();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var store = app.Services.GetRequiredService<IBatchStateStore>();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var envelope = new TelemetryBatchEnvelope("1", Guid.NewGuid(), new string('a', 32), "PRIVATE_PRODUCER", now,
            "text/plain; charset=utf-8", "gzip", 128, new string('a', 64), "v1");
        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.CreateAsync(envelope, now.AddDays(30), default).AsTask()));
        var batch = created[0];
        foreach (var target in new[] { BatchLifecycle.Publishing, BatchLifecycle.Accepted, BatchLifecycle.Processing })
        {
            var command = new BatchTransition(target, now.AddSeconds(batch.Version));
            var version = batch.Version;
            (await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.TryTransitionAsync(batch.BatchId, version, command, default).AsTask()))).ShouldAllBe(value => value);
            batch = (await store.GetAsync(batch.BatchId, default))!;
        }
        var id = Guid.NewGuid();
        var line = new LineProcessingOutcome(1, LineOutcome.Rejected, TelemetryErrorCodes.LpInvalidSyntax,
            new("PRIVATE_COMPANY"), new("PRIVATE_VEHICLE"), "hmac-sha256:v1:" + new string('a', 64), now, id,
            new(id, batch.BatchId, 1, TelemetryErrorCodes.LpInvalidSyntax, now, now.AddDays(30), new string('b', 64)));
        await store.RecordLineResultsAsync(batch.BatchId, [line], default);
        await store.RecordLineResultsAsync(batch.BatchId, [line], default);
        var replay = new LineProcessingOutcome(3, LineOutcome.IdenticalReplay, null,
            new("PRIVATE_COMPANY"), new("PRIVATE_VEHICLE"), "hmac-sha256:v1:" + new string('d', 64), now);
        await store.RecordLineResultsAsync(batch.BatchId, [replay], default);
        await store.RecordLineResultsAsync(batch.BatchId, [replay], default);
        // A new row followed by a conflicting existing row rolls back the whole page.
        var otherId = Guid.NewGuid();
        var other = line with
        {
            LineNumber = 2,
            QuarantineId = otherId,
            ProtectedContent = line.ProtectedContent! with
            {
                LineNumber = 2,
                QuarantineId = otherId
            }
        };
        await Should.ThrowAsync<ProviderOperationException>(() => store.RecordLineResultsAsync(batch.BatchId,
            [other, line with { SafeDigest = "hmac-sha256:v1:" + new string('c', 64) }], default).AsTask());
        var text = await client.GetStringAsync("/metrics");
        foreach (var state in new[] { "receiving", "publishing", "accepted", "processing" })
            Count(text, "telemetry_batch_state_" + state).ShouldBe(1);
        Count(text, "telemetry_rejections_lp_invalid_syntax").ShouldBe(1);
        Count(text, "telemetry_lines_identical_replay").ShouldBe(1);
        foreach (var secret in new[] { "PRIVATE_", batch.BatchId.ToString(), id.ToString(), "empresa_id=", "veiculo_id=", "batch_id=" })
            text.ShouldNotContain(secret);
        text.Split('\n').Where(x => x.StartsWith("telemetry_batch_state_", StringComparison.Ordinal) || x.StartsWith("telemetry_rejections_", StringComparison.Ordinal))
            .ShouldAllBe(x => !x.Contains("empresa", StringComparison.Ordinal) && !x.Contains("vehicle", StringComparison.Ordinal));
        await using var db = database.CreateContext();
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDeleteAsync(db.Batches.Where(x => x.BatchId == batch.BatchId.Value));
        var after = await client.GetStringAsync("/metrics");
        Count(after, "telemetry_rejections_lp_invalid_syntax").ShouldBe(1);
        Count(after, "telemetry_batch_state_receiving").ShouldBe(1);
    }
    private static double Count(string text, string prefix)
    {
        var line = text.Split('\n').Single(x => x.StartsWith(prefix, StringComparison.Ordinal));
        return double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
    }
}
