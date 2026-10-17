using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Configuration;

namespace CentralMobilidade.Telemetry.Infrastructure.Queue.PubSub;

/// <summary>Unary pull with one outstanding bounded body per enumeration. SDK clients belong to the caller.</summary>
public sealed class PubSubBatchQueue : IDurableBatchQueue, IAsyncDisposable
{
    private const int MaxBody = 8 * 1024 * 1024;
    private static readonly string[] EnvelopeKeys = ["schemaVersion", "batchId", "traceId", "producerId", "receivedAt",
        "contentType", "contentEncoding", "compressedBytes", "payloadSha256", "contractVersion"];
    private static readonly RetrySettings NoRetry = RetrySettings.FromConstantBackoff(1, TimeSpan.FromMilliseconds(1), _ => false);
    private readonly PublisherServiceApiClient publisher;
    private readonly SubscriberServiceApiClient subscriber;
    private readonly PubSubQueueOptions options;
    private readonly SemaphoreSlim publishGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Delivery, byte> outstanding = new();
    private int disposed;

    public PubSubBatchQueue(PublisherServiceApiClient publisher, SubscriberServiceApiClient subscriber, IConfiguration configuration)
    {
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.subscriber = subscriber ?? throw new ArgumentNullException(nameof(subscriber));
        options = PubSubQueueOptions.Read(configuration ?? throw new ArgumentNullException(nameof(configuration)));
    }

    public async ValueTask<DurablePublishReceipt> PublishAsync(TelemetryBatchEnvelope metadata, Stream gzipBody, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(gzipBody);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        try
        {
            metadata.Validate();
        }
        catch (ValidationException) { throw InvalidEnvelope(); }
        if (!gzipBody.CanRead)
            throw InvalidEnvelope();
        await publishGate.WaitAsync(cancellationToken);
        byte[]? bytes = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            deadline.CancelAfter(options.PublishTimeout);
            bytes = new byte[checked((int)metadata.CompressedBytes)];
            await gzipBody.ReadExactlyAsync(bytes, deadline.Token);
            if (await gzipBody.ReadAsync(new byte[1], deadline.Token) != 0)
                throw InvalidEnvelope();
            ValidateBody(metadata, bytes);
            var message = new PubsubMessage { Data = ByteString.CopyFrom(bytes) };
            message.Attributes.Add(Attributes(metadata));
            await PublishConfirmed(options.Topic, message, deadline.Token);
            return new(new BatchId(metadata.BatchId), DateTimeOffset.UtcNow);
        }
        catch (EndOfStreamException) { throw InvalidEnvelope(); }
        catch (ProviderOperationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0) { throw; }
        catch (Exception error) { throw Failure(error); }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
            publishGate.Release();
        }
    }

    public IAsyncEnumerable<IQueueDelivery> ConsumeAsync(CancellationToken cancellationToken) => ConsumeCore(null, TimeSpan.FromSeconds(5), cancellationToken);

    public IAsyncEnumerable<IQueueDelivery> ConsumeAsync(Action<bool> reportAvailability, TimeSpan heartbeatInterval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportAvailability);
        if (heartbeatInterval < TimeSpan.FromMilliseconds(100) || heartbeatInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        return ConsumeCore(reportAvailability, heartbeatInterval, cancellationToken);
    }

    private async IAsyncEnumerable<IQueueDelivery> ConsumeCore(Action<bool>? reportAvailability, TimeSpan heartbeatInterval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var monitoring = reportAvailability is null ? Task.CompletedTask : Monitor(reportAvailability, heartbeatInterval, session);
        try
        {
            while (true)
            {
                session.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                var response = await Pull(session.Token);
                if (response is null || response.ReceivedMessages.Count == 0)
                {
                    await Task.Delay(20, session.Token);
                    continue;
                }
                // MaxMessages=1 is also a memory/backpressure boundary; never buffer an unbounded inbox.
                if (response.ReceivedMessages.Count != 1)
                    throw Failure();
                var received = response.ReceivedMessages[0];
                if (string.IsNullOrEmpty(received.AckId) || received.Message is null || received.Message.Data.Length > MaxBody)
                    throw InvalidEnvelope(); // No ACK of an unbounded or unidentifiable message.
                await using var delivery = new Delivery(this, received);
                outstanding.TryAdd(delivery, 0);
                var yielded = false;
                try
                {
                    await delivery.StartLease(session.Token);
                    if (!delivery.Valid)
                        await delivery.DeadLetterAsync("INVALID_ENVELOPE", session.Token);
                    else if (delivery.DeliveryAttempt > options.MaxAttempts || delivery.Metadata.ReceivedAt.AddDays(7) <= DateTimeOffset.UtcNow)
                        await delivery.DeadLetterAsync("RETRY_EXHAUSTED", session.Token);
                    else
                    {
                        yielded = true;
                        yield return delivery;
                        await delivery.Finished.Task.WaitAsync(session.Token);
                        delivery.CheckLease();
                    }
                }
                finally
                {
                    if (yielded && !delivery.Finished.Task.IsCompleted && options.DrainTimeout > TimeSpan.Zero)
                        await Task.WhenAny(delivery.Finished.Task, Task.Delay(options.DrainTimeout));
                    outstanding.TryRemove(delivery, out _);
                }
            }
        }
        finally
        {
            await session.CancelAsync();
            await monitoring;
        }
    }

    private async Task Monitor(Action<bool> report, TimeSpan interval, CancellationTokenSource session)
    {
        try
        {
            while (true)
            {
                var subscription = await Invoke(settings => subscriber.GetSubscriptionAsync(
                    new GetSubscriptionRequest { Subscription = options.Subscription }, settings), session.Token);
                if (subscription.Topic != options.Topic || subscription.Detached)
                    throw Failure();
                if (!session.IsCancellationRequested)
                    report(true);
                await Task.Delay(interval, session.Token);
            }
        }
        catch (Exception) { await session.CancelAsync(); }
        finally { report(false); }
    }

    private async Task<PullResponse?> Pull(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.PullTimeout);
        try
        {
            return await subscriber.PullAsync(new PullRequest { Subscription = options.Subscription, MaxMessages = 1 },
                Settings(deadline.Token, options.PullTimeout)).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (RpcException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return null; }
        catch (RpcException error) when (error.StatusCode == StatusCode.DeadlineExceeded) { return null; }
        catch (Exception error) { throw Failure(error); }
    }

    private async Task PublishConfirmed(string topic, PubsubMessage message, CancellationToken token)
    {
        var response = await Invoke(settings => publisher.PublishAsync(new PublishRequest { Topic = topic, Messages = { message } }, settings), token);
        if (response.MessageIds.Count != 1 || string.IsNullOrWhiteSpace(response.MessageIds[0]))
            throw Failure();
    }

    private async Task<T> Invoke<T>(Func<CallSettings, Task<T>> operation, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(options.PublishTimeout);
        try
        {
            return await operation(Settings(deadline.Token, options.PublishTimeout)).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (RpcException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception error) { throw Failure(error); }
    }

    private Task Invoke(Func<CallSettings, Task> operation, CancellationToken token) => Invoke(async settings =>
    {
        await operation(settings);
        return true;
    }, token);

    private Task Ack(string ack, CancellationToken token) => Invoke(settings => subscriber.AcknowledgeAsync(
        new AcknowledgeRequest { Subscription = options.Subscription, AckIds = { ack } }, settings), token);

    private Task Lease(string ack, int seconds, CancellationToken token) => Invoke(settings => subscriber.ModifyAckDeadlineAsync(
        new ModifyAckDeadlineRequest { Subscription = options.Subscription, AckIds = { ack }, AckDeadlineSeconds = seconds }, settings), token);

    private static CallSettings Settings(CancellationToken token, TimeSpan timeout) => CallSettings.FromCancellationToken(token)
        .WithTimeout(timeout).WithRetry(NoRetry);

    private static Dictionary<string, string> Attributes(TelemetryBatchEnvelope m)
    {
        var result = new Dictionary<string, string>
        {
            ["schemaVersion"] = m.SchemaVersion,
            ["batchId"] = m.BatchId.ToString("D"),
            ["traceId"] = m.TraceId,
            ["producerId"] = m.ProducerId,
            ["receivedAt"] = m.ReceivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["contentType"] = m.ContentType,
            ["contentEncoding"] = m.ContentEncoding,
            ["compressedBytes"] = m.CompressedBytes.ToString(CultureInfo.InvariantCulture),
            ["payloadSha256"] = m.PayloadSha256
        };
        if (m.ContractVersion is not null)
            result["contractVersion"] = m.ContractVersion;
        return result;
    }

    private static void ValidateBody(TelemetryBatchEnvelope m, byte[] bytes)
    {
        if (bytes.Length != m.CompressedBytes || bytes.Length < 10 || bytes[0] != 0x1f || bytes[1] != 0x8b || bytes[2] != 8
            || (bytes[3] & 0xe0) != 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(m.PayloadSha256)))
            throw InvalidEnvelope();
    }

    private static ProviderOperationException InvalidEnvelope() => new(TelemetryErrorCodes.RequestInvalid, ProviderFailureKind.Permanent);
    private static ProviderOperationException Failure(Exception? error = null) => new(TelemetryErrorCodes.DurabilityUnconfirmed, error is RpcException rpc
        ? rpc.StatusCode switch
        {
            StatusCode.PermissionDenied or StatusCode.Unauthenticated or StatusCode.InvalidArgument or StatusCode.NotFound
                or StatusCode.FailedPrecondition or StatusCode.OutOfRange or StatusCode.Unimplemented => ProviderFailureKind.Permanent,
            StatusCode.Unavailable or StatusCode.ResourceExhausted or StatusCode.Aborted => ProviderFailureKind.Transient,
            _ => ProviderFailureKind.Ambiguous
        } : ProviderFailureKind.Ambiguous);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        await lifetime.CancelAsync();
        await publishGate.WaitAsync();
        publishGate.Release();
        foreach (var delivery in outstanding.Keys)
            await delivery.DisposeAsync();
        // Injected SDK clients/channels are not ours to shut down.
    }

    private sealed class Delivery : IQueueDelivery
    {
        private readonly PubSubBatchQueue owner;
        private readonly string ack;
        private readonly byte[] bytes;
        private readonly Dictionary<string, string> attributes;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly CancellationTokenSource renewalStop = new();
        private readonly CancellationTokenSource leaseLost = new();
        private Task renewal = Task.CompletedTask;
        private string? action;
        private bool disposed;
        internal bool Valid
        {
            get; private set;
        }
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TelemetryBatchEnvelope Metadata { get; private set; } = null!;
        public Stream Body
        {
            get;
        }
        public int DeliveryAttempt { get; private set; } = 1;

        internal Delivery(PubSubBatchQueue owner, ReceivedMessage received)
        {
            this.owner = owner;
            ack = received.AckId;
            bytes = received.Message.Data.ToByteArray();
            Body = new MemoryStream(bytes, writable: false);
            attributes = received.Message.Attributes.Where(p => EnvelopeKeys.Contains(p.Key) && p.Value.Length <= 512)
                .ToDictionary(p => p.Key, p => p.Value);
            try
            {
                string Text(string key) => attributes[key];
                Metadata = new(Text("schemaVersion"), Guid.ParseExact(Text("batchId"), "D"), Text("traceId"), Text("producerId"),
                    DateTimeOffset.ParseExact(Text("receivedAt"), "O", CultureInfo.InvariantCulture), Text("contentType"), Text("contentEncoding"),
                    long.Parse(Text("compressedBytes"), NumberStyles.None, CultureInfo.InvariantCulture), Text("payloadSha256"), attributes.GetValueOrDefault("contractVersion"));
                Metadata.Validate();
                ValidateBody(Metadata, bytes);
                if (Metadata.ReceivedAt > DateTimeOffset.UtcNow.AddMinutes(5))
                    throw InvalidEnvelope();
                if (received.Message.Attributes.TryGetValue("deliveryAttempt", out var attempt))
                {
                    DeliveryAttempt = int.Parse(attempt, NumberStyles.None, CultureInfo.InvariantCulture);
                    if (DeliveryAttempt < 1)
                        throw InvalidEnvelope();
                }
                DeliveryAttempt = Math.Max(DeliveryAttempt, received.DeliveryAttempt);
                if (DeliveryAttempt < 1)
                    throw InvalidEnvelope();
                Valid = true;
            }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or KeyNotFoundException or ValidationException or ProviderOperationException)
            {
                Valid = false;
            }
        }

        internal async Task StartLease(CancellationToken token)
        {
            await owner.Lease(ack, owner.options.AckDeadlineSeconds, token);
            renewal = Renew();
        }

        private async Task Renew()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(renewalStop.Token, owner.lifetime.Token);
            limit.CancelAfter(owner.options.MaxLeaseDuration);
            try
            {
                while (true)
                {
                    await Task.Delay(owner.options.LeaseRenewalInterval, limit.Token);
                    await owner.Lease(ack, owner.options.AckDeadlineSeconds, limit.Token);
                }
            }
            catch (Exception)
            {
                if (!renewalStop.IsCancellationRequested)
                {
                    await leaseLost.CancelAsync();
                    Finished.TrySetResult();
                }
            }
        }

        internal void CheckLease()
        {
            if (leaseLost.IsCancellationRequested)
                throw Failure();
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken) => Settle("complete", TimeSpan.Zero, null, cancellationToken);
        public ValueTask RetryAsync(TimeSpan delay, CancellationToken cancellationToken) => Settle("retry", delay, null, cancellationToken);
        public ValueTask DeadLetterAsync(string errorCode, CancellationToken cancellationToken) => Settle("dead", TimeSpan.Zero, errorCode, cancellationToken);

        private async ValueTask Settle(string next, TimeSpan delay, string? error, CancellationToken token)
        {
            if (delay < TimeSpan.Zero || delay > TimeSpan.FromMinutes(10))
                throw new ArgumentOutOfRangeException(nameof(delay));
            await gate.WaitAsync(token);
            try
            {
                if (action is not null)
                {
                    if (action != next)
                        throw new InvalidOperationException("Delivery already settled with another action.");
                    return;
                }
                ObjectDisposedException.ThrowIf(disposed, this);
                CheckLease();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.lifetime.Token, leaseLost.Token);
                try
                {
                    var dead = next == "dead" || next == "retry" && DeliveryAttempt >= owner.options.MaxAttempts;
                    if (next == "retry" && !dead)
                    {
                        var exponential = Math.Min(owner.options.MaxRetryDelay.TotalMilliseconds,
                            owner.options.RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(DeliveryAttempt - 1, 30)) * (0.8 + Random.Shared.NextDouble() * 0.4));
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(owner.options.MaxRetryDelay.TotalMilliseconds, Math.Max(delay.TotalMilliseconds, exponential))), linked.Token);
                    }
                    if (next != "complete")
                    {
                        var message = new PubsubMessage { Data = ByteString.CopyFrom(bytes) };
                        message.Attributes.Add(attributes);
                        message.Attributes["deliveryAttempt"] = (dead ? DeliveryAttempt : checked(DeliveryAttempt + 1)).ToString(CultureInfo.InvariantCulture);
                        if (dead)
                            message.Attributes["errorCode"] = next == "retry" ? "RETRY_EXHAUSTED" : SafeCode(error);
                        // Confirm the copy before ACK. Ambiguous results may duplicate, never justify dropping the original.
                        await owner.PublishConfirmed(dead ? owner.options.DeadLetterTopic : owner.options.Topic, message, linked.Token);
                    }
                    CheckLease();
                    await owner.Ack(ack, linked.Token);
                    action = next;
                    await renewalStop.CancelAsync();
                    Finished.TrySetResult();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                catch (ProviderOperationException) { throw; }
                catch (Exception errorThrown) { throw Failure(errorThrown); }
            }
            finally { gate.Release(); }
        }

        private static string SafeCode(string? code) => TelemetryErrorCodes.IsRejectionCode(code)
            || TelemetryErrorCodes.TryGetProblem(code, out _) || code is "INVALID_ENVELOPE" or "RETRY_EXHAUSTED" or "POISON_BATCH"
            ? code! : "POISON_BATCH";

        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (disposed)
                    return;
                disposed = true;
                await renewalStop.CancelAsync();
                await renewal;
                if (action is null)
                {
                    // Best-effort NACK; if unavailable, provider lease expiry remains the fallback. Never ACK here.
                    try
                    {
                        await owner.Lease(ack, 0, CancellationToken.None);
                    }
                    catch (ProviderOperationException) { }
                }
                await Body.DisposeAsync();
                CryptographicOperations.ZeroMemory(bytes);
                Finished.TrySetResult();
                owner.outstanding.TryRemove(this, out _);
            }
            finally { gate.Release(); }
        }
    }
}

internal sealed class PubSubQueueOptions
{
    internal string Topic = "", Subscription = "", DeadLetterTopic = "";
    internal TimeSpan PublishTimeout = TimeSpan.FromSeconds(5), PullTimeout = TimeSpan.FromSeconds(30),
        DrainTimeout = TimeSpan.FromSeconds(30), RetryDelay = TimeSpan.FromSeconds(1), MaxRetryDelay = TimeSpan.FromMinutes(1),
        LeaseRenewalInterval = TimeSpan.FromSeconds(20), MaxLeaseDuration = TimeSpan.FromMinutes(30);
    internal int MaxAttempts = 10, AckDeadlineSeconds = 60;

    internal static PubSubQueueOptions Read(IConfiguration configuration)
    {
        try
        {
            var section = configuration.GetSection("Queue:PubSub");
            string Resource(string key, string kind)
            {
                var value = section[key] ?? "";
                var parts = value.Split('/');
                if (value.Length > 512 || parts.Length != 4 || parts[0] != "projects" || parts[2] != kind
                    || parts[1].Length == 0 || parts[3].Length == 0 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '-' and not '_' and not '.' and not '~' and not '+' and not '%')
                    || section["ProjectId"] is string project && parts[1] != project)
                    throw new ArgumentException();
                return value;
            }
            var o = new PubSubQueueOptions
            {
                Topic = Resource("Topic", "topics"),
                Subscription = Resource("Subscription", "subscriptions"),
                DeadLetterTopic = Resource("DeadLetterTopic", "topics"),
                PublishTimeout = section.GetValue<TimeSpan?>("PublishTimeout") ?? TimeSpan.FromSeconds(5),
                PullTimeout = section.GetValue<TimeSpan?>("PullTimeout") ?? TimeSpan.FromSeconds(30),
                DrainTimeout = section.GetValue<TimeSpan?>("DrainTimeout") ?? TimeSpan.FromSeconds(30),
                RetryDelay = section.GetValue<TimeSpan?>("RetryDelay") ?? TimeSpan.FromSeconds(1),
                MaxRetryDelay = section.GetValue<TimeSpan?>("MaxRetryDelay") ?? TimeSpan.FromMinutes(1),
                LeaseRenewalInterval = section.GetValue<TimeSpan?>("LeaseRenewalInterval") ?? TimeSpan.FromSeconds(20),
                MaxLeaseDuration = section.GetValue<TimeSpan?>("MaxLeaseDuration") ?? TimeSpan.FromMinutes(30),
                AckDeadlineSeconds = section.GetValue<int?>("AckDeadlineSeconds") ?? 60,
                MaxAttempts = section.GetValue<int?>("MaxAttempts") ?? 10
            };
            if (o.Topic == o.DeadLetterTopic || o.PublishTimeout < TimeSpan.FromMilliseconds(1) || o.PublishTimeout > TimeSpan.FromMinutes(1)
                || o.PullTimeout < TimeSpan.FromMilliseconds(1) || o.PullTimeout > TimeSpan.FromMinutes(1)
                || o.DrainTimeout < TimeSpan.Zero || o.DrainTimeout > TimeSpan.FromMinutes(5) || o.MaxAttempts is < 1 or > 1000
                || o.AckDeadlineSeconds is < 10 or > 600 || o.LeaseRenewalInterval < TimeSpan.FromMilliseconds(100)
                || o.LeaseRenewalInterval > TimeSpan.FromSeconds(o.AckDeadlineSeconds / 2d)
                || o.MaxLeaseDuration < TimeSpan.FromSeconds(o.AckDeadlineSeconds) || o.MaxLeaseDuration > TimeSpan.FromHours(1)
                || o.RetryDelay < TimeSpan.FromMilliseconds(1) || o.MaxRetryDelay < o.RetryDelay || o.MaxRetryDelay > TimeSpan.FromMinutes(10))
                throw new ArgumentException();
            return o;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ArgumentException("Invalid Pub/Sub queue configuration.");
        }
    }
}
