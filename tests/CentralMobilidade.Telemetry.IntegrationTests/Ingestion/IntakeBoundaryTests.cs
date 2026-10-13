using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Ingestion;

// Tests run the production entry point. Queue/state doubles isolate HTTP intake from provider durability.
[Trait("Category", "Integration")]
[Trait("Feature", "US1Intake")]
[Collection("Observability")]
public sealed class IntakeBoundaryTests
{
    private static readonly byte[] Gzip = Compress("telemetria,empresa_id=company,veiculo_id=vehicle latitude=1.0,longitude=2.0,velocidade=0.0,rpm=0i,ignicao=false 1791849600000\n");

    [Theory]
    [InlineData(-1, true, 413)]
    [InlineData(0, true, 202)]
    [InlineData(1, true, 202)]
    [InlineData(-1, false, 413)]
    [InlineData(0, false, 202)]
    [InlineData(1, false, 202)]
    public async Task Compressed_limit_applies_at_and_around_the_boundary_with_or_without_content_length(int limitOffset, bool declaredLength, int status)
    {
        await using var fixture = new IntakeFixture(Gzip.Length + limitOffset);
        var response = await fixture.Send(new MemoryStream(Gzip), declaredLength ? Gzip.Length : null);
        if (status == 413)
        {
            await AssertRejected(fixture, response, status, TelemetryErrorCodes.PayloadCompressedLimit);
        }
        else
        {
            response.Response.StatusCode.ShouldBe(status, "Valid intake must be mapped and durably accepted");
            fixture.Queue.Bodies.ShouldHaveSingleItem().ShouldBe(Gzip);
            var metadata = fixture.Queue.Metadata.ShouldHaveSingleItem();
            metadata.CompressedBytes.ShouldBe(Gzip.Length);
            metadata.PayloadSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Gzip)));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("application/octet-stream")]
    [InlineData("text/plain; charset=iso-8859-1")]
    [InlineData("text/plain; charset=utf-16")]
    [InlineData("text/plain; charset=unknown")]
    [InlineData("text/plain; charset=utf-8; charset=utf-16")]
    public async Task Unsupported_content_type_or_charset_is_rejected_before_publication(string? contentType)
    {
        await using var fixture = new IntakeFixture();
        var response = await fixture.Send(new MemoryStream(Gzip), Gzip.Length, contentType: contentType);
        await AssertRejected(fixture, response, 415, TelemetryErrorCodes.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("text/plain; charset=\"utf-8\"")]
    [InlineData("TEXT/PLAIN; CHARSET=UTF-8")]
    public async Task Valid_media_type_and_charset_variants_preserve_the_original_gzip_bytes(string contentType)
    {
        await using var fixture = new IntakeFixture();
        var response = await fixture.Send(new MemoryStream(Gzip), Gzip.Length, contentType: contentType);
        response.Response.StatusCode.ShouldBe(202);
        fixture.Queue.Bodies.ShouldHaveSingleItem().ShouldBe(Gzip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("identity")]
    [InlineData("br")]
    [InlineData("gzip, br")]
    public async Task Missing_or_unsupported_content_encoding_is_rejected(string? encoding)
    {
        await using var fixture = new IntakeFixture();
        var response = await fixture.Send(new MemoryStream(Gzip), Gzip.Length, encoding: encoding);
        await AssertRejected(fixture, response, 415, TelemetryErrorCodes.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("v2")]
    [InlineData(null)]
    public async Task Missing_or_unknown_contract_version_is_rejected(string? version)
    {
        await using var fixture = new IntakeFixture();
        var response = await fixture.Send(new MemoryStream(Gzip), Gzip.Length, version: version);
        await AssertRejected(fixture, response, 400, TelemetryErrorCodes.RequestInvalid);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("plain")]
    [InlineData("short-header")]
    [InlineData("wrong-magic")]
    [InlineData("wrong-method")]
    [InlineData("reserved-flags")]
    public async Task Empty_body_or_invalid_gzip_header_does_not_enter_the_queue(string variant)
    {
        var bytes = variant switch
        {
            "empty" => Array.Empty<byte>(),
            "plain" => Encoding.UTF8.GetBytes("not a gzip payload"),
            "short-header" => Gzip[..9],
            _ => (byte[])Gzip.Clone()
        };
        if (variant == "wrong-magic")
            bytes[0] = 0;
        if (variant == "wrong-method")
            bytes[2] = 0;
        if (variant == "reserved-flags")
            bytes[3] |= 0xe0;
        await using var fixture = new IntakeFixture();
        var response = await fixture.Send(new MemoryStream(bytes), null);
        await AssertRejected(fixture, response, 400, TelemetryErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task Upload_timeout_cancels_a_blocked_read_and_prevents_publication()
    {
        await using var fixture = new IntakeFixture(uploadTimeout: TimeSpan.FromMilliseconds(150));
        using var body = new BlockingBody(Gzip[..10]);
        var response = await fixture.Send(body, null).WaitAsync(TimeSpan.FromSeconds(5));
        await AssertRejected(fixture, response, 408, TelemetryErrorCodes.UploadTimeout);
        body.ReadStarted.Task.IsCompleted.ShouldBeTrue("The timeout must occur during body intake");
        body.ReadCancelled.Task.IsCompleted.ShouldBeTrue("Upload timeout must cancel the blocked stream read");
    }

    [Fact]
    public async Task Client_disconnect_cancels_body_intake_without_publication()
    {
        await using var fixture = new IntakeFixture(uploadTimeout: TimeSpan.FromSeconds(10));
        using var body = new BlockingBody(Gzip[..10]);
        using var abort = new CancellationTokenSource();
        var request = fixture.Send(body, null, cancellationToken: abort.Token);
        var first = await Task.WhenAny(body.ReadStarted.Task, request).WaitAsync(TimeSpan.FromSeconds(5));
        if (first == request)
        {
            var premature = await request;
            premature.Response.StatusCode.ShouldNotBe(404, "T034/T035 must map the intake endpoint");
        }
        first.ShouldBe(body.ReadStarted.Task, "The request must reach body intake before disconnecting");
        abort.Cancel();
        try
        {
            var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
            response.Response.StatusCode.ShouldNotBe(202);
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
        await body.ReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Queue.Bodies.ShouldBeEmpty();
        fixture.Store.AcceptedCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("10.10.0.2", "203.0.113.7", "https", "203.0.113.7", "https")]
    [InlineData("10.20.0.5", "203.0.113.7", "https", "203.0.113.7", "https")]
    [InlineData("10.20.1.5", "203.0.113.7", "https", "10.20.1.5", "http")]
    [InlineData("198.51.100.20", "203.0.113.7", "https", "198.51.100.20", "http")]
    [InlineData("10.10.0.2", "203.0.113.7, 198.51.100.20", "https, http", "198.51.100.20", "http")]
    [InlineData("10.10.0.2", "203.0.113.7", "https, http", "10.10.0.2", "http")]
    public async Task Forwarded_headers_require_known_peers_symmetric_headers_and_a_bounded_chain(string peer, string forwardedFor, string forwardedProto, string expectedIp, string expectedScheme)
    {
        await using var fixture = new IntakeFixture();
        var response = await fixture.Send(new MemoryStream(Gzip), Gzip.Length, peer: peer, forwardedFor: forwardedFor, forwardedProto: forwardedProto);
        response.Response.StatusCode.ShouldNotBe(404, "Proxy assertions must exercise the production intake route");
        fixture.Observer.RemoteIp.ShouldBe(expectedIp);
        fixture.Observer.Scheme.ShouldBe(expectedScheme);
        fixture.Observer.Host.ShouldBe("localhost", "Forwarded host must not be trusted by this policy");
    }

    private static async Task AssertRejected(IntakeFixture fixture, HttpContext context, int status, string code)
    {
        context.Response.StatusCode.ShouldBe(status, "The production intake endpoint must reject this boundary case");
        context.Response.ContentType.ShouldStartWith("application/problem+json");
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.GetProperty("code").GetString().ShouldBe(code);
        json.RootElement.GetProperty("status").GetInt32().ShouldBe(status);
        fixture.Queue.Bodies.ShouldBeEmpty("Rejected intake must not publish a batch");
        fixture.Store.AcceptedCount.ShouldBe(0);
    }

    private static byte[] Compress(string value)
    {
        using var result = new MemoryStream();
        using (var gzip = new GZipStream(result, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(value));
        return result.ToArray();
    }

    private sealed class IntakeFixture(long maxCompressedBytes = 1024, TimeSpan? uploadTimeout = null) : WebApplicationFactory<ProducerBearerHandler>
    {
        private static readonly string Token = OpaqueBearerTokens.Generate(BearerCredentialKind.Producer);
        private static readonly BearerTokenHash Hash = BearerTokenHash.Create(Token);
        internal QueueSpy Queue { get; } = new();
        internal StateSpy Store { get; } = new();
        internal RequestObserver Observer { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "localhost",
                ["ConnectionStrings:TelemetryControl"] = "",
                ["Telemetry:MaxCompressedBytes"] = maxCompressedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Telemetry:UploadTimeout"] = (uploadTimeout ?? TimeSpan.FromSeconds(3)).ToString("c"),
                ["Observability:OtlpTraceEndpoint"] = null,
                ["Authentication:Producers:0:CredentialId"] = "intake-test",
                ["Authentication:Producers:0:ProducerId"] = "boundary-producer",
                ["Authentication:Producers:0:Enabled"] = "true",
                ["Authentication:Producers:0:Hash:LookupDigest"] = Hash.LookupDigest,
                ["Authentication:Producers:0:Hash:Salt"] = Hash.Salt,
                ["Authentication:Producers:0:Hash:Verifier"] = Hash.Verifier,
                ["Authentication:Producers:0:Hash:Iterations"] = Hash.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDurableBatchQueue>();
                services.AddSingleton<IDurableBatchQueue>(Queue);
                services.RemoveAll<IBatchStateStore>();
                services.AddSingleton<IBatchStateStore>(Store);
                services.AddSingleton<IStartupFilter>(Observer);
                services.Configure<ForwardedHeadersOptions>(options =>
                {
                    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                    options.ForwardLimit = 1;
                    options.RequireHeaderSymmetry = true;
                    options.KnownProxies.Clear();
                    options.KnownIPNetworks.Clear();
                    options.KnownProxies.Add(IPAddress.Parse("10.10.0.2"));
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.20.0.0/24"));
                });
            });
        }

        internal async Task<HttpContext> Send(Stream body, long? length, string? contentType = "text/plain; charset=utf-8", string? encoding = "gzip", string? version = "v1", string peer = "198.51.100.20", string? forwardedFor = null, string? forwardedProto = null, CancellationToken cancellationToken = default)
        {
            using var client = CreateClient(); // Starts the actual Program pipeline before using TestServer's stream API.
            using var input = new NonSeekableBody(body);
            return await Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                context.Request.Method = "POST";
                context.Request.Path = "/api/v1/telemetry/batches";
                context.Request.Scheme = "http";
                context.Request.Host = new HostString("localhost");
                context.Request.Body = input;
                context.Request.ContentLength = length;
                context.Request.Headers.Authorization = "Bearer " + Token;
                if (contentType is not null)
                    context.Request.ContentType = contentType;
                if (encoding is not null)
                    context.Request.Headers.ContentEncoding = encoding;
                if (version is not null)
                    context.Request.Headers["X-Contract-Version"] = version;
                if (forwardedFor is not null)
                    context.Request.Headers["X-Forwarded-For"] = forwardedFor;
                if (forwardedProto is not null)
                    context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
                context.Request.Headers["X-Forwarded-Host"] = "spoofed.invalid";
            }, cancellationToken);
        }
    }

    private sealed class NonSeekableBody(Stream source) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("Network body length is not available from the stream.");
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => source.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => source.ReadAsync(buffer, offset, count, cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Intake must read asynchronously.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                source.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class RequestObserver : IStartupFilter
    {
        internal string? RemoteIp
        {
            get; private set;
        }
        internal string? Scheme
        {
            get; private set;
        }
        internal string? Host
        {
            get; private set;
        }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                await continuation(context);
                RemoteIp = context.Connection.RemoteIpAddress?.ToString();
                Scheme = context.Request.Scheme;
                Host = context.Request.Host.Value;
            });
            next(app);
        };
    }

    private sealed class QueueSpy : IDurableBatchQueue
    {
        internal List<byte[]> Bodies { get; } = [];
        internal List<TelemetryBatchEnvelope> Metadata { get; } = [];
        public async ValueTask<DurablePublishReceipt> PublishAsync(TelemetryBatchEnvelope metadata, Stream gzipBody, CancellationToken cancellationToken)
        {
            using var bytes = new MemoryStream();
            await gzipBody.CopyToAsync(bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Bodies.Add(bytes.ToArray());
            Metadata.Add(metadata);
            return new(new BatchId(metadata.BatchId), DateTimeOffset.UtcNow);
        }
        public IAsyncEnumerable<IQueueDelivery> ConsumeAsync(CancellationToken cancellationToken) => throw new NotSupportedException("Intake never consumes deliveries.");
    }

    private sealed class StateSpy : IBatchStateStore
    {
        private readonly Dictionary<BatchId, BatchSnapshot> batches = [];
        internal int AcceptedCount => batches.Values.Count(batch => batch.State == BatchLifecycle.Accepted);
        public ValueTask<BatchSnapshot> CreateAsync(TelemetryBatchEnvelope metadata, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new BatchSnapshot(new(metadata.BatchId), metadata.ProducerId, BatchLifecycle.Receiving, metadata.ReceivedAt, expiresAt, null, null, new(0, 0, 0, null), new Dictionary<string, long>(), [], false, 0);
            batches.TryAdd(batch.BatchId, batch);
            return ValueTask.FromResult(batches[batch.BatchId]);
        }
        public ValueTask<BatchSnapshot?> GetAsync(BatchId batchId, CancellationToken cancellationToken) => ValueTask.FromResult(batches.GetValueOrDefault(batchId));
        public ValueTask<bool> TryTransitionAsync(BatchId batchId, long expectedVersion, BatchTransition transition, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!batches.TryGetValue(batchId, out var current) || current.Version != expectedVersion)
                return ValueTask.FromResult(false);
            batches[batchId] = current with
            {
                State = transition.TargetState,
                Version = current.Version + 1
            };
            return ValueTask.FromResult(true);
        }
        public ValueTask RecordLineResultsAsync(BatchId batchId, IReadOnlyCollection<LineProcessingOutcome> results, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<RejectionResultPage> GetRejectionsAsync(BatchId batchId, string? cursor, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class BlockingBody(byte[] prefix) : Stream
    {
        private int offset;
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - offset);
                prefix.AsMemory(offset, count).CopyTo(buffer);
                offset += count;
                return count;
            }
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException) { ReadCancelled.TrySetResult(); throw; }
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Intake must read asynchronously.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
