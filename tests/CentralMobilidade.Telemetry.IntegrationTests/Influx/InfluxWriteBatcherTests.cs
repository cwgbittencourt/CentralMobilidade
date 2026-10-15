using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Processing;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Influx;

public sealed class InfluxWriteBatcherTests
{
    private static readonly byte[] Line = Encoding.UTF8.GetBytes("telemetria,empresa_id=a,veiculo_id=b rpm=1i 1\n");
    private static TelemetryWritePoint Point(int number) => new(new BatchId(Guid.NewGuid()), number, new(new("a"), new("b"), EventTimestamp.FromUnixTimeMilliseconds(number)));
    private static TelemetryOptions Options(int count = 5000, long bytes = 2 * 1024 * 1024) => new()
    {
        WriteBatchMaxPoints = count,
        WriteBatchMaxBytes = bytes,
        WriteBatchFlushInterval = TimeSpan.FromHours(1)
    };

    [Fact]
    public async Task Byte_overflow_flushes_before_next_line_and_disposal_drains_the_remainder()
    {
        var writer = new Writer();
        var outcomes = new List<TelemetryWriteRequest>();
        var batcher = new InfluxWriteBatcher(writer, Options(bytes: Line.Length * 2 - 1), TimeProvider.System,
            (request, result, token) => { outcomes.Add(request); return ValueTask.CompletedTask; });
        var first = Point(17);
        var second = Point(29);
        await batcher.AddAsync(first, Line, default);
        writer.Requests.ShouldBeEmpty();
        await batcher.AddAsync(second, Line, default);
        writer.Requests.Count.ShouldBe(1);
        await batcher.DisposeAsync();
        await batcher.DisposeAsync();
        writer.Requests.Count.ShouldBe(2);
        outcomes.SelectMany(r => r.Points).ShouldBe(new[] { first, second });
        foreach (var request in writer.Requests)
            request.UncompressedBytes.ShouldBe(Line.Length);
        writer.Payloads.All(bytes => bytes.SequenceEqual(Line)).ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(() => batcher.AddAsync(Point(3), Line, default).AsTask());
    }

    [Fact]
    public async Task Timer_measures_age_of_oldest_point_and_flushes_without_another_add()
    {
        var clock = new ManualTime();
        var writer = new Writer();
        await using var batcher = new InfluxWriteBatcher(writer, new TelemetryOptions(), clock);
        await batcher.AddAsync(Point(1), Line, default);
        clock.Advance(TimeSpan.FromMilliseconds(900));
        writer.Requests.ShouldBeEmpty();
        await batcher.AddAsync(Point(2), Line, default);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await writer.Written.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await batcher.FlushAsync();
        writer.Requests.ShouldHaveSingleItem().Points.Count.ShouldBe(2);
        clock.Advance(TimeSpan.FromSeconds(10));
        writer.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Input_memory_is_copied_before_add_returns_and_empty_flush_writes_nothing()
    {
        var writer = new Writer();
        await using var batcher = new InfluxWriteBatcher(writer, Options(), TimeProvider.System);
        await batcher.FlushAsync();
        writer.Requests.ShouldBeEmpty();
        var line = Line.ToArray();
        await batcher.AddAsync(Point(1), line, default);
        Array.Fill(line, (byte)'x');
        await batcher.FlushAsync();
        writer.Payloads.ShouldHaveSingleItem().ShouldBe(Line);
    }

    [Fact]
    public async Task Backpressure_serializes_writes_and_cancelled_wait_does_not_poison_current_batch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new Writer { BeforeWrite = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        await using var batcher = new InfluxWriteBatcher(writer, Options(count: 1), TimeProvider.System);
        var first = batcher.AddAsync(Point(1), Line, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancel = new CancellationTokenSource();
        var second = batcher.AddAsync(Point(2), Line, cancel.Token).AsTask();
        second.IsCompleted.ShouldBeFalse();
        cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => second);
        release.TrySetResult();
        await first;
        await batcher.AddAsync(Point(3), Line, default);
        writer.Requests.Count.ShouldBe(2);
        writer.Requests.SelectMany(r => r.Points).Select(p => p.LineNumber).ShouldBe(new[] { 1, 3 });
    }

    [Fact]
    public async Task Timer_failure_is_observed_by_flush_and_disposal_without_retry()
    {
        var clock = new ManualTime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new Writer { BeforeWrite = _ => { entered.TrySetResult(); throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Ambiguous); } };
        var batcher = new InfluxWriteBatcher(writer, new TelemetryOptions(), clock);
        await batcher.AddAsync(Point(1), Line, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Should.ThrowAsync<ProviderOperationException>(() => batcher.FlushAsync().AsTask());
        await Should.ThrowAsync<ProviderOperationException>(() => batcher.AddAsync(Point(2), Line, default).AsTask());
        await Should.ThrowAsync<ProviderOperationException>(() => batcher.DisposeAsync().AsTask());
        writer.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Partial_results_reach_awaited_callback_with_original_owners()
    {
        var first = Point(17);
        var rejected = Point(29);
        var writer = new Writer { Result = request => new(1, [new(rejected.BatchId, rejected.LineNumber, TelemetryErrorCodes.StorageSchemaConflict)]) };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var batcher = new InfluxWriteBatcher(writer, Options(count: 2), TimeProvider.System, async (request, result, token) =>
        {
            request.Points.ShouldBe(new[] { first, rejected });
            result.Rejections.ShouldHaveSingleItem().LineNumber.ShouldBe(29);
            observed.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        await batcher.AddAsync(first, Line, default);
        var write = batcher.AddAsync(rejected, Line, default).AsTask();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        write.IsCompleted.ShouldBeFalse();
        release.TrySetResult();
        await write;
    }

    [Theory]
    [InlineData("count")]
    [InlineData("owner")]
    [InlineData("callback")]
    public async Task Invalid_outcomes_or_failed_outcome_recording_fault_the_batcher(string mode)
    {
        var writer = new Writer
        {
            Result = request => mode == "count" ? new(0, []) : mode == "owner"
            ? new(0, [new(new BatchId(Guid.NewGuid()), 1, TelemetryErrorCodes.StorageRejected)]) : new(1, [])
        };
        var batcher = new InfluxWriteBatcher(writer, Options(count: 1), TimeProvider.System, (_, _, _) =>
            throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Ambiguous));
        await Should.ThrowAsync<ProviderOperationException>(() => batcher.AddAsync(Point(1), Line, default).AsTask());
        await Should.ThrowAsync<ProviderOperationException>(() => batcher.DisposeAsync().AsTask());
        writer.Attempts.ShouldBe(1);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("newline")]
    [InlineData("duplicate")]
    public async Task Invalid_line_or_owner_does_not_discard_already_buffered_work(string mode)
    {
        var writer = new Writer();
        await using var batcher = new InfluxWriteBatcher(writer, Options(bytes: Line.Length * 2), TimeProvider.System);
        var first = Point(1);
        await batcher.AddAsync(first, Line, default);
        var invalid = mode == "oversize" ? new byte[Line.Length * 2 + 1] : mode == "newline" ? Encoding.UTF8.GetBytes("two\nlines\n") : Line;
        await Should.ThrowAsync<ArgumentException>(() => batcher.AddAsync(mode == "duplicate" ? first : Point(2), invalid, default).AsTask());
        await batcher.FlushAsync();
        writer.Requests.ShouldHaveSingleItem().Points.ShouldHaveSingleItem().ShouldBe(first);
    }

    private sealed class Writer : ITelemetryWriter
    {
        internal int Attempts;
        internal readonly List<TelemetryWriteRequest> Requests = [];
        internal readonly List<byte[]> Payloads = [];
        internal readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<CancellationToken, Task>? BeforeWrite;
        internal Func<TelemetryWriteRequest, TelemetryWriteResult>? Result;
        public async ValueTask<TelemetryWriteResult> WriteAsync(TelemetryWriteRequest request, Stream gzipLineProtocol, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            if (BeforeWrite is not null)
                await BeforeWrite(cancellationToken);
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(gzipLineProtocol, CompressionMode.Decompress, true))
                await gzip.CopyToAsync(output, cancellationToken);
            Requests.Add(request);
            Payloads.Add(output.ToArray());
            Written.TrySetResult();
            return Result?.Invoke(request) ?? new(request.Points.Count, []);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long ticks;
        private Timer? timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => timer = new(this, callback, state);
        internal void Advance(TimeSpan amount)
        {
            ticks += amount.Ticks;
            timer?.Fire();
        }
        private sealed class Timer(ManualTime clock, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                return true;
            }
            internal void Fire()
            {
                if (clock.ticks >= due)
                {
                    due = long.MaxValue;
                    callback(state);
                }
            }
            public void Dispose() => due = long.MaxValue;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
