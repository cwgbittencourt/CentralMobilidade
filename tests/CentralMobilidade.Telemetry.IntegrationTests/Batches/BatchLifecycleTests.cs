using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Batches;

[Trait("Category", "Integration")]
public sealed class BatchLifecycleTests(BatchLifecycleDatabase database) : IClassFixture<BatchLifecycleDatabase>
{
    [Fact]
    public async Task Overlapping_concurrent_result_pages_count_each_line_and_rejection_once()
    {
        var batch = await Create();
        var lines = Mixed(batch);
        await Race(12, async index =>
        {
            var page = (index % 3) switch
            {
                0 => lines,
                1 => [lines[2], lines[0]],
                _ => [lines[1], lines[2]]
            };
            await database.Store().RecordLineResultsAsync(batch.BatchId, page, default);
            return true;
        });
        var snapshot = await Read(batch);
        snapshot.Counters.ShouldBe(new BatchCounters(1, 1, 1, null));
        snapshot.ErrorSummary.ShouldHaveSingleItem().ShouldBe(new KeyValuePair<string, long>(TelemetryErrorCodes.LpInvalidSyntax, 1));
        snapshot.Companies.Select(company => company.Value).Order().ShouldBe(new[] { "company-a", "company-b" });
        snapshot.CompanyScopeComplete.ShouldBeFalse();
        await using var db = database.CreateContext();
        (await db.LineResults.CountAsync(row => row.BatchId == batch.BatchId.Value)).ShouldBe(3);
        (await db.QuarantinedRejections.CountAsync(row => row.BatchId == batch.BatchId.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task Conflicting_concurrent_pages_have_one_winner_and_rollback_the_entire_losing_page()
    {
        var batch = await Create();
        var common = Line(batch, 1);
        var pages = new[] { new[] { Line(batch, 2), common }, new[] { Line(batch, 3), common with { Outcome = LineOutcome.IdenticalReplay } } };
        var results = await Race(2, async index =>
        {
            try
            {
                await database.Store().RecordLineResultsAsync(batch.BatchId, pages[index], default);
                return true;
            }
            catch (ProviderOperationException error)
            {
                error.Kind.ShouldBe(ProviderFailureKind.Permanent);
                error.ErrorCode.ShouldBe(TelemetryErrorCodes.StorageRejected);
                return false;
            }
        });
        results.Count(result => result).ShouldBe(1);
        var winner = Array.IndexOf(results, true);
        var snapshot = await Read(batch);
        snapshot.Counters.ShouldBe(winner == 0 ? new(2, 0, 0, null) : new(1, 0, 1, null));
        await using var db = database.CreateContext();
        var numbers = await db.LineResults.Where(row => row.BatchId == batch.BatchId.Value).Select(row => row.LineNumber).ToArrayAsync();
        numbers.Order().ShouldBe(pages[winner].Select(line => (long)line.LineNumber).Order());
    }

    [Fact]
    public async Task A_conflicting_page_does_not_leave_partial_counters_or_orphan_quarantine_metadata()
    {
        var batch = await Create();
        var original = Line(batch, 1);
        await database.Store().RecordLineResultsAsync(batch.BatchId, [original], default);
        var rejected = Mixed(batch)[2];
        await Should.ThrowAsync<ProviderOperationException>(() => database.Store().RecordLineResultsAsync(batch.BatchId,
            [rejected, original with { Outcome = LineOutcome.IdenticalReplay }], default).AsTask());
        (await Read(batch)).Counters.ShouldBe(new(1, 0, 0, null));
        await using var db = database.CreateContext();
        (await db.LineResults.CountAsync(row => row.BatchId == batch.BatchId.Value)).ShouldBe(1);
        (await db.QuarantinedRejections.CountAsync(row => row.BatchId == batch.BatchId.Value)).ShouldBe(0);
    }

    [Theory]
    [InlineData(BatchLifecycle.Completed)]
    [InlineData(BatchLifecycle.CompletedWithRejections)]
    public async Task Concurrent_exact_terminal_replays_do_not_inflate_counters_or_advance_version_twice(BatchLifecycle state)
    {
        var batch = await Create();
        var lines = state == BatchLifecycle.Completed ? new[] { Line(batch, 1) } : Mixed(batch);
        await database.Store().RecordLineResultsAsync(batch.BatchId, lines, default);
        var before = await Read(batch);
        var counters = before.Counters with
        {
            Total = lines.Length
        };
        var transition = new BatchTransition(state, batch.ReceivedAt.AddSeconds(5), counters);
        (await Race(12, _ => database.Store().TryTransitionAsync(batch.BatchId, before.Version, transition, default).AsTask())).ShouldAllBe(result => result);
        var after = await Read(batch);
        after.State.ShouldBe(state);
        after.Version.ShouldBe(before.Version + 1);
        after.Counters.ShouldBe(counters);
        after.CompletedAt.ShouldBe(transition.OccurredAt);
        after.CompanyScopeComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Competing_retry_failure_and_completion_commands_have_exactly_one_optimistic_winner()
    {
        var batch = await Create();
        await database.Store().RecordLineResultsAsync(batch.BatchId, Mixed(batch), default);
        var before = await Read(batch);
        var counters = new BatchCounters(1, 1, 1, 3);
        var at = batch.ReceivedAt.AddSeconds(5);
        BatchTransition[] commands = [new(BatchLifecycle.Retrying, at, counters), new(BatchLifecycle.Failed, at, counters, TelemetryErrorCodes.StorageRejected), new(BatchLifecycle.CompletedWithRejections, at, counters)];
        var results = await Race(commands.Length, index => database.Store().TryTransitionAsync(batch.BatchId, before.Version, commands[index], default).AsTask());
        results.Count(result => result).ShouldBe(1);
        var winner = commands[Array.IndexOf(results, true)];
        var after = await Read(batch);
        after.State.ShouldBe(winner.TargetState);
        after.Version.ShouldBe(before.Version + 1);
        after.Counters.ShouldBe(counters);
        await using var db = database.CreateContext();
        var row = await db.Batches.AsNoTracking().SingleAsync(row => row.BatchId == batch.BatchId.Value);
        row.RetryCount.ShouldBe(winner.TargetState == BatchLifecycle.Retrying ? 1 : 0);
        row.TerminalErrorCode.ShouldBe(winner.ErrorCode);
    }

    [Theory]
    [InlineData(BatchLifecycle.Completed, BatchLifecycle.Processing)]
    [InlineData(BatchLifecycle.CompletedWithRejections, BatchLifecycle.Processing)]
    [InlineData(BatchLifecycle.Failed, BatchLifecycle.Processing)]
    [InlineData(BatchLifecycle.Expired, BatchLifecycle.Accepted)]
    [InlineData(BatchLifecycle.Rejected, BatchLifecycle.Receiving)]
    [InlineData(BatchLifecycle.PublishFailed, BatchLifecycle.Publishing)]
    public async Task Terminal_states_are_absorbing_but_exact_command_replay_is_safe(BatchLifecycle terminal, BatchLifecycle source)
    {
        var batch = await Create(source);
        if (terminal == BatchLifecycle.CompletedWithRejections)
            await database.Store().RecordLineResultsAsync(batch.BatchId, Mixed(batch), default);
        else if (terminal == BatchLifecycle.Completed)
            await database.Store().RecordLineResultsAsync(batch.BatchId, [Line(batch, 1)], default);
        batch = await Read(batch);
        var at = terminal == BatchLifecycle.Expired ? batch.ExpiresAt.AddSeconds(1) : batch.ReceivedAt.AddSeconds(5);
        var counters = batch.Counters with
        {
            Total = terminal is BatchLifecycle.Completed or BatchLifecycle.CompletedWithRejections ? batch.Counters.Accepted + batch.Counters.Rejected + batch.Counters.Replayed : null
        };
        var command = new BatchTransition(terminal, at, counters);
        (await database.Store().TryTransitionAsync(batch.BatchId, batch.Version, command, default)).ShouldBeTrue();
        var before = await Read(batch);
        var results = await Race(Enum.GetValues<BatchLifecycle>().Length, index => database.Store().TryTransitionAsync(batch.BatchId,
            before.Version, new(Enum.GetValues<BatchLifecycle>()[index], at.AddSeconds(1), new(1, 0, 0, 1)), default).AsTask());
        results.ShouldAllBe(result => !result);
        (await database.Store().TryTransitionAsync(batch.BatchId, batch.Version, command, default)).ShouldBeTrue();
        (await database.Store().TryTransitionAsync(batch.BatchId, batch.Version, command with
        {
            Counters = new(2, 0, 0, 2)
        }, default)).ShouldBeFalse();
        (await database.Store().TryTransitionAsync(batch.BatchId, batch.Version, command with
        {
            OccurredAt = at.AddMilliseconds(1)
        }, default)).ShouldBeFalse();
        var after = await Read(batch);
        after.State.ShouldBe(terminal);
        after.Version.ShouldBe(before.Version);
        after.Counters.ShouldBe(before.Counters);
        after.CompletedAt.ShouldBe(before.CompletedAt);
    }

    [Theory]
    [InlineData(BatchLifecycle.Completed)]
    [InlineData(BatchLifecycle.Failed)]
    public async Task Terminal_batches_allow_existing_line_replay_but_reject_new_results(BatchLifecycle terminal)
    {
        var batch = await Create();
        var line = Line(batch, 1);
        await database.Store().RecordLineResultsAsync(batch.BatchId, [line], default);
        batch = await Read(batch);
        (await database.Store().TryTransitionAsync(batch.BatchId, batch.Version, new(terminal, batch.ReceivedAt.AddSeconds(5), new(1, 0, 0, 1)), default)).ShouldBeTrue();
        await Race(8, async index =>
        {
            if (index % 2 == 0)
                await database.Store().RecordLineResultsAsync(batch.BatchId, [line], default);
            else
                await Should.ThrowAsync<ProviderOperationException>(() => database.Store().RecordLineResultsAsync(batch.BatchId, [Line(batch, index + 1)], default).AsTask());
            return true;
        });
        var snapshot = await Read(batch);
        snapshot.State.ShouldBe(terminal);
        snapshot.Counters.ShouldBe(new(1, 0, 0, 1));
        snapshot.Version.ShouldBe(batch.Version + 1);
    }

    [Fact]
    public async Task Independent_EF_contexts_reject_a_stale_row_version_instead_of_losing_an_update()
    {
        var batch = await Create();
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var a = await first.Batches.SingleAsync(row => row.BatchId == batch.BatchId.Value);
        var b = await second.Batches.SingleAsync(row => row.BatchId == batch.BatchId.Value);
        a.RetryCount = 1;
        a.RowVersion++;
        await first.SaveChangesAsync();
        b.RetryCount = 2;
        b.RowVersion++;
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await second.Entry(b).ReloadAsync();
        b.RetryCount.ShouldBe(1);
        b.RowVersion.ShouldBe(a.RowVersion);
    }

    [Fact]
    public async Task Cancelled_line_recording_does_not_commit_any_results()
    {
        var batch = await Create();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => database.Store().RecordLineResultsAsync(batch.BatchId, Mixed(batch), cancellation.Token).AsTask());
        var after = await Read(batch);
        after.Counters.ShouldBe(new(0, 0, 0, null));
        after.ErrorSummary.ShouldBeEmpty();
        after.Companies.ShouldBeEmpty();
    }

    [Fact]
    [Trait("PendingImplementation", "T058")]
    public async Task Concurrent_resume_replays_keep_the_original_start_time_and_one_retry_increment()
    {
        var batch = await Create();
        var retry = new BatchTransition(BatchLifecycle.Retrying, batch.ReceivedAt.AddSeconds(5));
        (await Race(8, _ => database.Store().TryTransitionAsync(batch.BatchId, batch.Version, retry, default).AsTask())).ShouldAllBe(result => result);
        var retrying = await Read(batch);
        var resume = new BatchTransition(BatchLifecycle.Processing, batch.ReceivedAt.AddSeconds(6));
        var resumed = await Race(8, _ => database.Store().TryTransitionAsync(batch.BatchId, retrying.Version, resume, default).AsTask());
        resumed.ShouldAllBe(result => result, "Exact retries of the same resume command must succeed without replacing the original processing start time");
        var after = await Read(batch);
        after.Version.ShouldBe(batch.Version + 2);
        after.ProcessingStartedAt.ShouldBe(batch.ProcessingStartedAt);
        await using var db = database.CreateContext();
        (await db.Batches.AsNoTracking().SingleAsync(row => row.BatchId == batch.BatchId.Value)).RetryCount.ShouldBe(1);
    }

    [Fact]
    public async Task Stale_aggregate_snapshot_cannot_finalize_over_newly_recorded_line_results()
    {
        var batch = await Create();
        await database.Store().RecordLineResultsAsync(batch.BatchId, [Line(batch, 1)], default);
        var stale = await Read(batch);
        // Deterministic competing-writer schedule: another transaction commits after the snapshot.
        await database.Store().RecordLineResultsAsync(batch.BatchId, [Line(batch, 2)], default);
        var before = await Read(batch);
        (await database.Store().TryTransitionAsync(batch.BatchId, stale.Version,
            new(BatchLifecycle.Completed, batch.ReceivedAt.AddSeconds(5), stale.Counters with
            {
                Total = 1
            }), default))
            .ShouldBeFalse("Finalization based on stale counters must not overwrite the second committed line");
        var after = await Read(batch);
        after.State.ShouldBe(BatchLifecycle.Processing);
        after.Counters.ShouldBe(before.Counters);
        after.Counters.Accepted.ShouldBe(2);
    }

    private async Task<BatchSnapshot> Create(BatchLifecycle state = BatchLifecycle.Processing)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var metadata = new TelemetryBatchEnvelope("1", Guid.NewGuid(), new string('a', 32), "lifecycle-test", now,
            "text/plain; charset=utf-8", "gzip", 128, new string('a', 64), "v1");
        var store = database.Store();
        var batch = await store.CreateAsync(metadata, now.AddDays(30), default);
        foreach (var target in new[] { BatchLifecycle.Publishing, BatchLifecycle.Accepted, BatchLifecycle.Processing })
        {
            if (batch.State == state)
                break;
            (await store.TryTransitionAsync(batch.BatchId, batch.Version, new(target, now.AddSeconds(batch.Version)), default)).ShouldBeTrue();
            batch = (await store.GetAsync(batch.BatchId, default))!;
        }
        batch.State.ShouldBe(state);
        return batch;
    }
    private async Task<BatchSnapshot> Read(BatchSnapshot batch) => (await database.Store().GetAsync(batch.BatchId, default)).ShouldNotBeNull();
    private static LineProcessingOutcome Line(BatchSnapshot batch, int number) => new(number, LineOutcome.Accepted, null,
        new("company-a"), new("vehicle-a"), "hmac-sha256:v1:" + new string('a', 64), batch.ReceivedAt);
    private static LineProcessingOutcome[] Mixed(BatchSnapshot batch)
    {
        var id = Guid.NewGuid();
        return [Line(batch, 1), Line(batch, 2) with { Outcome = LineOutcome.IdenticalReplay, CompanyId = new("company-b") },
            new(3, LineOutcome.Rejected, TelemetryErrorCodes.LpInvalidSyntax, new("company-a"), new("vehicle-a"),
                "hmac-sha256:v1:" + new string('b', 64), batch.ReceivedAt, id,
                new(id, batch.BatchId, 3, TelemetryErrorCodes.LpInvalidSyntax, batch.ReceivedAt, batch.ReceivedAt.AddDays(30), new string('c', 64)))];
    }
    private static async Task<T[]> Race<T>(int count, Func<int, Task<T>> action)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(async index => { await start.Task; return await action(index); }).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
    }
}

/// <summary>One disposable PostgreSQL per class; each scenario uses distinct batch identifiers.</summary>
public sealed class BatchLifecycleDatabase : IAsyncLifetime
{
    private MigratedPostgresFixture postgres = null!;
    private ServiceProvider services = null!;
    internal TelemetryControlDbContext CreateContext() => postgres.Create();
    internal IBatchStateStore Store() => new PostgresBatchStateStore(services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>());
    public async Task InitializeAsync()
    {
        postgres = await MigratedPostgresFixture.StartAsync();
        services = new ServiceCollection().AddTelemetryControlPersistence(postgres.Connection).BuildServiceProvider();
    }
    public async Task DisposeAsync()
    {
        if (services is not null)
            await services.DisposeAsync();
        if (postgres is not null)
            await postgres.DisposeAsync();
    }
}
