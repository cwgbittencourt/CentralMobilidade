using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.ContractTests.Queue;

// Linked into provider integration projects: no provider SDK or in-memory queue belongs here.
public abstract class DurableBatchQueueContractTests
{
    protected abstract Task<DurableQueueContractHarness> CreateAsync(QueuePublishFault fault = QueuePublishFault.None);
    private static readonly byte[] Payload = Compress();

    [Theory]
    [InlineData(null)]
    [InlineData("v1")]
    public async Task Native_gzip_body_and_versioned_attributes_are_preserved(string? contractVersion)
    {
        await using var harness = await CreateAsync();
        var metadata = Envelope() with
        {
            ContractVersion = contractVersion
        };
        using var body = new MemoryStream(Payload, writable: false);
        var before = DateTimeOffset.UtcNow;
        var receipt = await harness.Publisher.PublishAsync(metadata, body, default);
        receipt.BatchId.Value.ShouldBe(metadata.BatchId);
        receipt.ConfirmedAt.Offset.ShouldBe(TimeSpan.Zero);
        receipt.ConfirmedAt.ShouldBeInRange(before, DateTimeOffset.UtcNow);
        body.CanRead.ShouldBeTrue("The publisher must leave the caller-owned stream open.");
        var native = (await harness.ReadNativeAsync(deadLetter: false, wait: true)).ShouldNotBeNull();
        AssertNative(native, metadata);
        native.Attributes.ContainsKey("contractVersion").ShouldBe(contractVersion is not null);
    }

    [Fact]
    public async Task Round_trip_keeps_two_distinct_batch_and_trace_correlations()
    {
        await using var harness = await CreateAsync();
        var first = Envelope();
        var second = Envelope() with
        {
            TraceId = new string('b', 32),
            ProducerId = "another-producer"
        };
        foreach (var metadata in new[] { first, second })
        {
            using var body = new MemoryStream(Payload);
            await harness.Publisher.PublishAsync(metadata, body, default);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var iterator = harness.ConsumeAsync(deadline.Token).GetAsyncEnumerator();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < 2; i++)
        {
            (await iterator.MoveNextAsync()).ShouldBeTrue();
            await using var delivery = iterator.Current;
            seen.Add(delivery.Metadata.BatchId).ShouldBeTrue();
            delivery.Metadata.ShouldBe(delivery.Metadata.BatchId == first.BatchId ? first : second);
            await AssertBodyAsync(delivery);
            await delivery.CompleteAsync(deadline.Token);
        }
        seen.ShouldBe(new[] { first.BatchId, second.BatchId }, ignoreOrder: true);
    }

    [Fact]
    public async Task Send_completion_alone_cannot_produce_a_durable_receipt()
    {
        await using var harness = await CreateAsync(QueuePublishFault.HoldConfirmation);
        using var body = new MemoryStream(Payload);
        var metadata = Envelope();
        var publish = harness.Publisher.PublishAsync(metadata, body, default).AsTask();
        await harness.PublishEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        publish.IsCompleted.ShouldBeFalse("Sending bytes is not the provider confirmation boundary.");
        await harness.ConfirmAsync();
        (await publish.WaitAsync(TimeSpan.FromSeconds(5))).BatchId.Value.ShouldBe(metadata.BatchId);
    }

    [Theory]
    [InlineData(QueuePublishFault.NegativeConfirmation, ProviderFailureKind.Transient)]
    [InlineData(QueuePublishFault.MissingConfirmation, ProviderFailureKind.Ambiguous)]
    public async Task Unconfirmed_publication_maps_to_a_safe_provider_failure(QueuePublishFault fault, ProviderFailureKind kind)
    {
        await using var harness = await CreateAsync(fault);
        using var body = new MemoryStream(Payload);
        var error = await Should.ThrowAsync<ProviderOperationException>(() => harness.Publisher.PublishAsync(Envelope(), body, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        error.Kind.ShouldBe(kind);
        error.ErrorCode.ShouldBe(TelemetryErrorCodes.DurabilityUnconfirmed);
        // Ambiguous confirmation does not imply that the provider failed to store the message.
    }

    [Theory]
    [InlineData("size")]
    [InlineData("digest")]
    [InlineData("schema")]
    [InlineData("encoding")]
    [InlineData("contract")]
    public async Task Invalid_metadata_or_body_integrity_is_permanent_and_not_published(string invalid)
    {
        await using var harness = await CreateAsync();
        var metadata = Envelope();
        metadata = invalid switch
        {
            "size" => metadata with { CompressedBytes = Payload.Length + 1 },
            "digest" => metadata with { PayloadSha256 = new string('0', 64) },
            "schema" => metadata with { SchemaVersion = "2" },
            "encoding" => metadata with { ContentEncoding = "identity" },
            _ => metadata with { ContractVersion = "v2" }
        };
        using var body = new MemoryStream(Payload);
        var error = await Should.ThrowAsync<ProviderOperationException>(() => harness.Publisher.PublishAsync(metadata, body, default).AsTask());
        error.Kind.ShouldBe(ProviderFailureKind.Permanent);
        error.ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
        (await harness.ReadNativeAsync(deadLetter: false, wait: false)).ShouldBeNull();
    }

    [Fact]
    public async Task Caller_cancellation_remains_cancellation_without_a_receipt()
    {
        await using var harness = await CreateAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var body = new MemoryStream(Payload);
        await Should.ThrowAsync<OperationCanceledException>(() => harness.Publisher.PublishAsync(Envelope(), body, cancelled.Token).AsTask());
        body.CanRead.ShouldBeTrue();
        (await harness.ReadNativeAsync(deadLetter: false, wait: false)).ShouldBeNull();
    }

    [Fact]
    public async Task Disposing_an_unsettled_session_redelivers_without_implicit_completion()
    {
        await using var harness = await CreateAsync();
        var metadata = await PublishAsync(harness);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var iterator = harness.ConsumeAsync(deadline.Token).GetAsyncEnumerator())
        {
            (await iterator.MoveNextAsync()).ShouldBeTrue();
            await iterator.Current.DisposeAsync();
        }
        await using var retry = harness.ConsumeAsync(deadline.Token).GetAsyncEnumerator();
        (await retry.MoveNextAsync()).ShouldBeTrue();
        await using var delivery = retry.Current;
        delivery.Metadata.ShouldBe(metadata);
        delivery.DeliveryAttempt.ShouldBeGreaterThanOrEqualTo(1);
        await AssertBodyAsync(delivery);
        await delivery.CompleteAsync(deadline.Token);
    }

    [Fact]
    public async Task Explicit_retry_preserves_body_metadata_and_increases_attempt()
    {
        await using var harness = await CreateAsync();
        var metadata = await PublishAsync(harness);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var iterator = harness.ConsumeAsync(deadline.Token).GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await using var first = iterator.Current;
        var attempt = first.DeliveryAttempt;
        await first.RetryAsync(TimeSpan.FromMilliseconds(50), deadline.Token);
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await using var second = iterator.Current;
        second.Metadata.ShouldBe(metadata);
        second.DeliveryAttempt.ShouldBeGreaterThan(attempt);
        await AssertBodyAsync(second);
        await second.CompleteAsync(deadline.Token);
    }

    [Fact]
    public async Task Manual_completion_is_idempotent_and_removes_the_delivery()
    {
        await using var harness = await CreateAsync();
        await PublishAsync(harness);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var iterator = harness.ConsumeAsync(deadline.Token).GetAsyncEnumerator())
        {
            (await iterator.MoveNextAsync()).ShouldBeTrue();
            await using var delivery = iterator.Current;
            await delivery.CompleteAsync(deadline.Token);
            await delivery.CompleteAsync(deadline.Token);
        }
        using var quietWindow = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await using var verification = harness.ConsumeAsync(quietWindow.Token).GetAsyncEnumerator();
        await Should.ThrowAsync<OperationCanceledException>(() => verification.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Dead_letter_keeps_original_bytes_and_correlation_with_a_safe_error_code()
    {
        await using var harness = await CreateAsync();
        var metadata = await PublishAsync(harness);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var iterator = harness.ConsumeAsync(deadline.Token).GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await using var delivery = iterator.Current;
        await delivery.DeadLetterAsync("LP_INVALID_SYNTAX", deadline.Token);
        var native = (await harness.ReadNativeAsync(deadLetter: true, wait: true)).ShouldNotBeNull();
        AssertNative(native, metadata);
        native.Attributes["errorCode"].ShouldBe("LP_INVALID_SYNTAX");
    }

    private static async Task<TelemetryBatchEnvelope> PublishAsync(DurableQueueContractHarness harness)
    {
        var metadata = Envelope();
        using var body = new MemoryStream(Payload);
        await harness.Publisher.PublishAsync(metadata, body, default);
        return metadata;
    }

    private static async Task AssertBodyAsync(IQueueDelivery delivery)
    {
        using var bytes = new MemoryStream();
        await delivery.Body.CopyToAsync(bytes);
        bytes.ToArray().ShouldBe(Payload);
    }

    private static void AssertNative(NativeQueueContractMessage native, TelemetryBatchEnvelope metadata)
    {
        native.Body.ShouldBe(Payload, "The provider body must be the original gzip, not JSON/Base64 or recompressed content.");
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["schemaVersion"] = metadata.SchemaVersion,
            ["batchId"] = metadata.BatchId.ToString("D"),
            ["traceId"] = metadata.TraceId,
            ["producerId"] = metadata.ProducerId,
            ["contentType"] = metadata.ContentType,
            ["contentEncoding"] = metadata.ContentEncoding,
            ["compressedBytes"] = metadata.CompressedBytes.ToString(CultureInfo.InvariantCulture),
            ["payloadSha256"] = metadata.PayloadSha256
        })
            native.Attributes[key].ShouldBe(value);
        DateTimeOffset.Parse(native.Attributes["receivedAt"], CultureInfo.InvariantCulture).ShouldBe(metadata.ReceivedAt);
        if (metadata.ContractVersion is not null)
            native.Attributes["contractVersion"].ShouldBe(metadata.ContractVersion);
    }

    private static TelemetryBatchEnvelope Envelope() => new("1", Guid.NewGuid(), new string('a', 32), "queue-contract", DateTimeOffset.UtcNow.AddMinutes(-5),
        "text/plain; charset=utf-8", "gzip", Payload.Length, Convert.ToHexStringLower(SHA256.HashData(Payload)), "v1");
    private static byte[] Compress()
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes("telemetria,empresa_id=valid-company,veiculo_id=vehicle-a latitude=1.0,longitude=2.0,velocidade=3.0,rpm=10i,ignicao=true 1720000000000\n"));
        return bytes.ToArray();
    }
}

public enum QueuePublishFault
{
    None, HoldConfirmation, NegativeConfirmation, MissingConfirmation
}
public sealed record NativeQueueContractMessage(byte[] Body, IReadOnlyDictionary<string, string> Attributes);
public abstract class DurableQueueContractHarness : IAsyncDisposable
{
    public abstract IDurableBatchQueue Publisher
    {
        get;
    }
    public abstract IAsyncEnumerable<IQueueDelivery> ConsumeAsync(CancellationToken token);
    // Observe the actual provider body/attributes, never values reconstructed from the expected envelope.
    public abstract Task<NativeQueueContractMessage?> ReadNativeAsync(bool deadLetter, bool wait);
    public abstract Task PublishEntered
    {
        get;
    }
    public abstract Task ConfirmAsync();
    public abstract ValueTask DisposeAsync();
}
