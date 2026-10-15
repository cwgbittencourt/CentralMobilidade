using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Contracts.Errors;

namespace CentralMobilidade.Telemetry.Application.Processing;

/// <summary>One bounded gzip batch and one in-flight write; durable outcome handling belongs to the caller.</summary>
public sealed class InfluxWriteBatcher : IAsyncDisposable
{
    private readonly ITelemetryWriter writer;
    private readonly TimeProvider time;
    private readonly int maxPoints, maxLineBytes;
    private readonly long maxBytes;
    private readonly TimeSpan interval;
    private readonly Func<TelemetryWriteRequest, TelemetryWriteResult, CancellationToken, ValueTask> completed;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ITimer timer;
    private readonly List<TelemetryWritePoint> points = [];
    private readonly HashSet<(Guid, int)> owners = [];
    private MemoryStream? compressed;
    private GZipStream? gzip;
    private long bytes, started;
    private string? traceId;
    private ExceptionDispatchInfo? failure;
    private int closing;
    private readonly object disposalSync = new();
    private Task? disposal;

    public InfluxWriteBatcher(ITelemetryWriter writer, TelemetryOptions options, TimeProvider timeProvider)
        : this(writer, options, timeProvider, static (_, _, _) => ValueTask.CompletedTask) { }

    public InfluxWriteBatcher(ITelemetryWriter writer, TelemetryOptions options, TimeProvider timeProvider,
        Func<TelemetryWriteRequest, TelemetryWriteResult, CancellationToken, ValueTask> completed)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(completed);
        maxPoints = options.WriteBatchMaxPoints;
        maxBytes = options.WriteBatchMaxBytes;
        maxLineBytes = options.MaxLineBytes;
        interval = options.WriteBatchFlushInterval;
        if (maxPoints <= 0 || maxBytes <= 0 || maxBytes > int.MaxValue - 65536 || maxLineBytes <= 0 ||
            interval <= TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentException("Invalid write batch limits.", nameof(options));
        this.writer = writer;
        time = timeProvider;
        this.completed = completed;
        timer = time.CreateTimer(state => { _ = TimerFlushAsync(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public async ValueTask AddAsync(TelemetryWritePoint point, ReadOnlyMemory<byte> line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (point.BatchId is null || point.Identity is null || point.LineNumber <= 0 || line.Length < 2 ||
            line.Length > maxBytes || line.Length > maxLineBytes || line.Span[^1] != (byte)'\n' || line.Span[..^1].Contains((byte)'\n'))
            throw new ArgumentException("A bounded complete Line Protocol line and owner are required.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            CheckAvailable();
            if (owners.Contains((point.BatchId.Value, point.LineNumber)))
                throw new ArgumentException("A write batch cannot repeat an owner.", nameof(point));
            if (points.Count > 0 && (bytes + line.Length > maxBytes || time.GetElapsedTime(started) >= interval))
                await FlushCore(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (points.Count == 0)
            {
                compressed = new MemoryStream();
                gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true);
                started = time.GetTimestamp();
                var currentTrace = Activity.Current?.TraceId ?? default;
                traceId = (currentTrace == default ? ActivityTraceId.CreateRandom() : currentTrace).ToHexString();
            }
            try
            {
                await gzip!.WriteAsync(line, cancellationToken);
                points.Add(point);
                owners.Add((point.BatchId.Value, point.LineNumber));
                bytes += line.Length;
                if (points.Count >= maxPoints || bytes >= maxBytes)
                    await FlushCore(cancellationToken);
                else if (points.Count == 1)
                {
                    var due = interval - time.GetElapsedTime(started);
                    timer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
            }
            catch (Exception error) { RecordFailure(error); throw; }
        }
        finally { gate.Release(); }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            CheckAvailable();
            await FlushCore(cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task TimerFlushAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (closing != 0 || failure is not null || points.Count == 0)
                return;
            var remaining = interval - time.GetElapsedTime(started);
            // A queued callback from the previous batch must not flush a newer batch early.
            if (remaining > TimeSpan.Zero)
                timer.Change(remaining, Timeout.InfiniteTimeSpan);
            else
                await FlushCore(CancellationToken.None);
        }
        catch (Exception error) { RecordFailure(error); }
        finally { gate.Release(); }
    }

    private async ValueTask FlushCore(CancellationToken token)
    {
        failure?.Throw();
        if (points.Count == 0)
            return;
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            await gzip!.DisposeAsync();
            gzip = null;
            compressed!.Position = 0;
            var request = new TelemetryWriteRequest(traceId!, Array.AsReadOnly(points.ToArray()), bytes);
            var result = await writer.WriteAsync(request, compressed, token);
            ValidateResult(request, result);
            await completed(request, result, token);
            points.Clear();
            owners.Clear();
            bytes = 0;
            await compressed.DisposeAsync();
            compressed = null;
        }
        catch (Exception error) { RecordFailure(error); throw; }
    }

    private static void ValidateResult(TelemetryWriteRequest request, TelemetryWriteResult result)
    {
        if (result is null || result.Rejections is null || result.CommittedPoints < 0 ||
            result.CommittedPoints > request.Points.Count || result.Rejections.Count != request.Points.Count - result.CommittedPoints)
            throw InvalidResult();
        var expected = new HashSet<(Guid, int)>();
        foreach (var point in request.Points)
            expected.Add((point.BatchId.Value, point.LineNumber));
        foreach (var rejection in result.Rejections)
            if (rejection is null || rejection.BatchId is null || !expected.Remove((rejection.BatchId.Value, rejection.LineNumber)) ||
                !TelemetryErrorCodes.IsRejectionCode(rejection.ErrorCode))
                throw InvalidResult();
    }
    private static ProviderOperationException InvalidResult() => new(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Ambiguous);
    private void RecordFailure(Exception error)
    {
        failure ??= ExceptionDispatchInfo.Capture(error);
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    private void CheckAvailable()
    {
        ObjectDisposedException.ThrowIf(closing != 0, this);
        failure?.Throw();
    }
    public ValueTask DisposeAsync()
    {
        lock (disposalSync)
            return new(disposal ??= DisposeCore());
    }
    private async Task DisposeCore()
    {
        Interlocked.Exchange(ref closing, 1);
        await gate.WaitAsync();
        try
        {
            try
            {
                await FlushCore(CancellationToken.None);
            }
            finally
            {
                await timer.DisposeAsync();
                if (gzip is not null)
                    await gzip.DisposeAsync();
                if (compressed is not null)
                    await compressed.DisposeAsync();
                gzip = null;
                compressed = null;
                points.Clear();
                owners.Clear();
            }
        }
        finally { gate.Release(); }
    }
}
