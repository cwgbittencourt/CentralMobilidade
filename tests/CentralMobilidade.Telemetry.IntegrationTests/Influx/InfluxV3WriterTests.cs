using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Ingestion;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Streaming;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Influx;

[Trait("Category", "Integration")]
public sealed class InfluxV3WriterTests
{
    private const string Line = "telemetria,empresa_id=company,veiculo_id=vehicle latitude=1.0,longitude=2.0,velocidade=0.0,rpm=0i,ignicao=false 1791686400123\n";

    [Fact]
    public async Task Production_writer_uses_native_gzip_and_explicit_durable_v3_parameters()
    {
        await using var server = await WriteServer.Start();
        using var client = new HttpClient();
        var writer = Writer(client, server.Url);
        var request = Request();
        using var gzip = Gzip(Line);
        var original = gzip.ToArray();
        var result = await writer.WriteAsync(request, gzip, CancellationToken.None);
        result.CommittedPoints.ShouldBe(1);
        result.Rejections.ShouldBeEmpty();
        server.Method.ShouldBe("POST");
        server.Path.ShouldBe("/api/v3/write_lp");
        server.Query["db"].ShouldBe("queryable");
        server.Query["precision"].ShouldBe("millisecond");
        server.Query["accept_partial"].ShouldBe("true");
        server.Query["no_sync"].ShouldBe("false");
        server.Authorization.ShouldBe("Bearer test-only-token");
        server.Encoding.ShouldBe("gzip");
        server.Body.ShouldBe(original);
        gzip.CanRead.ShouldBeTrue("The caller owns the input stream");
    }

    [Fact]
    public async Task Partial_write_maps_one_based_provider_line_to_original_batch_owner()
    {
        await using var server = await WriteServer.Start(400, """
            {"error":"partial write of line protocol occurred","data":[{"original_line":"invalid line","line_number":2,"error_message":"invalid column type"}]}
            """);
        using var client = new HttpClient();
        var writer = Writer(client, server.Url);
        var payload = Line + "invalid line\n" + Line;
        var request = Request(3) with
        {
            UncompressedBytes = Encoding.UTF8.GetByteCount(payload)
        };
        using var gzip = Gzip(payload);
        var result = await writer.WriteAsync(request, gzip, CancellationToken.None);
        result.CommittedPoints.ShouldBe(2);
        result.Rejections.Count.ShouldBe(1);
        result.Rejections[0].BatchId.ShouldBe(request.Points[1].BatchId);
        result.Rejections[0].LineNumber.ShouldBe(request.Points[1].LineNumber);
        result.Rejections[0].ErrorCode.ShouldBe(TelemetryErrorCodes.StorageSchemaConflict);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Non_success_is_never_counted_as_committed(int status)
    {
        await using var server = await WriteServer.Start(status, "SEEDED_PRIVATE_PROVIDER_BODY");
        using var client = new HttpClient();
        var writer = Writer(client, server.Url);
        using var gzip = Gzip(Line);
        var error = await Should.ThrowAsync<ProviderOperationException>(() => writer.WriteAsync(Request(), gzip, CancellationToken.None).AsTask());
        error.Message.ShouldNotContain("SEEDED");
        error.InnerException.ShouldBeNull();
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"error\":\"partial write of line protocol occurred\",\"data\":[]}")]
    [InlineData("{\"error\":\"partial write of line protocol occurred\",\"data\":[{\"line_number\":0}]}")]
    [InlineData("{\"error\":\"partial write of line protocol occurred\",\"data\":[{\"line_number\":9}]}")]
    public async Task Unreconcilable_partial_response_cannot_claim_success(string body)
    {
        await using var server = await WriteServer.Start(400, body);
        using var client = new HttpClient();
        var writer = Writer(client, server.Url);
        using var gzip = Gzip(Line);
        await Should.ThrowAsync<ProviderOperationException>(() => writer.WriteAsync(Request(), gzip, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("count")]
    [InlineData("bytes")]
    [InlineData("time")]
    public async Task Production_batcher_flushes_at_first_default_threshold(string trigger)
    {
        var capture = new CaptureWriter();
        var type = typeof(AcceptTelemetryBatchHandler).Assembly.GetType("CentralMobilidade.Telemetry.Application.Processing.InfluxWriteBatcher");
        type.ShouldNotBeNull("T050 must implement the bounded production batcher");
        var options = new TelemetryOptions();
        if (trigger != "time")
            options.WriteBatchFlushInterval = TimeSpan.FromHours(1); // Isolate count/byte limits from timer races.
        var batcher = Activator.CreateInstance(type, capture, options, TimeProvider.System).ShouldNotBeNull();
        try
        {
            var add = type.GetMethod("AddAsync", [typeof(TelemetryWritePoint), typeof(ReadOnlyMemory<byte>), typeof(CancellationToken)]).ShouldNotBeNull();
            var line = trigger == "bytes" ? Encoding.UTF8.GetBytes(new string('x', 8191) + "\n") : Encoding.UTF8.GetBytes(Line);
            var count = trigger == "count" ? 5000 : trigger == "bytes" ? 256 : 1;
            var elapsed = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                if (i == count - 1)
                    capture.Calls.ShouldBe(0, "No threshold has been reached before the final point");
                var operation = add.Invoke(batcher, [Point(i), new ReadOnlyMemory<byte>(line), CancellationToken.None]).ShouldBeOfType<ValueTask>();
                await operation;
            }
            await capture.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (trigger == "time")
                elapsed.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
            capture.Calls.ShouldBe(1);
            capture.Request!.Points.Count.ShouldBe(count);
            capture.Request.UncompressedBytes.ShouldBe((long)line.Length * count);
            capture.Bytes.ShouldBe(Enumerable.Repeat(line, count).SelectMany(bytes => bytes).ToArray());
        }
        finally
        {
            if (batcher is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }

    internal static ITelemetryWriter Writer(HttpClient client, string url)
    {
        var type = typeof(BoundedRequestSpool).Assembly.GetType("CentralMobilidade.Telemetry.Infrastructure.Influx.InfluxV3TelemetryWriter");
        type.ShouldNotBeNull("T049 must implement the production InfluxDB v3 writer");
        var values = new Dictionary<string, string?>
        {
            ["InfluxDb:Queryable:Url"] = url,
            ["InfluxDb:Queryable:Database"] = "queryable",
            ["InfluxDb:Queryable:Token"] = "test-only-token"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return Activator.CreateInstance(type, client, config).ShouldBeAssignableTo<ITelemetryWriter>()!;
    }

    private static TelemetryWritePoint Point(int index) => new(new BatchId(Guid.NewGuid()), 100 + index * 3,
        new EventIdentity(new CompanyId("company"), new VehicleId("vehicle-" + index), EventTimestamp.FromUnixTimeMilliseconds(1791686400123 + index)));
    private static TelemetryWriteRequest Request(int count = 1) =>
        new("0123456789abcdef0123456789abcdef", Enumerable.Range(0, count).Select(Point).ToArray(), Encoding.UTF8.GetByteCount(Line) * count);
    internal static MemoryStream Gzip(string body)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
            gzip.Write(Encoding.UTF8.GetBytes(body));
        output.Position = 0;
        return output;
    }

    private sealed class CaptureWriter : ITelemetryWriter
    {
        internal int Calls;
        internal TelemetryWriteRequest? Request;
        internal byte[]? Bytes;
        internal TaskCompletionSource Flushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<TelemetryWriteResult> WriteAsync(TelemetryWriteRequest request, Stream gzipLineProtocol, CancellationToken cancellationToken)
        {
            Request = request;
            using var decompressed = new MemoryStream();
            using var gzip = new GZipStream(gzipLineProtocol, CompressionMode.Decompress, true);
            await gzip.CopyToAsync(decompressed, cancellationToken);
            Bytes = decompressed.ToArray();
            Interlocked.Increment(ref Calls);
            Flushed.TrySetResult();
            return new(request.Points.Count, []);
        }
    }

    private sealed class WriteServer(WebApplication app) : IAsyncDisposable
    {
        internal string Url => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        internal string? Method, Path, Authorization, Encoding;
        internal Dictionary<string, string?> Query = [];
        internal byte[]? Body;
        internal static async Task<WriteServer> Start(int status = 204, string body = "")
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new WriteServer(app);
            app.MapPost("/api/v3/write_lp", async (HttpContext context) =>
            {
                server.Method = context.Request.Method;
                server.Path = context.Request.Path;
                server.Authorization = context.Request.Headers.Authorization.ToString();
                server.Encoding = context.Request.Headers.ContentEncoding.ToString();
                server.Query = context.Request.Query.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString());
                using var bytes = new MemoryStream();
                await context.Request.Body.CopyToAsync(bytes);
                server.Body = bytes.ToArray();
                context.Response.StatusCode = status;
                if (status != 204)
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(body);
                }
            });
            await app.StartAsync();
            return server;
        }
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

public sealed class InfluxRealServerTests : IClassFixture<InfluxContractServer>
{
    private readonly InfluxContractServer server;
    public InfluxRealServerTests(InfluxContractServer server) => this.server = server;

    [Fact]
    public async Task Production_writer_commits_to_real_server_and_preserves_millisecond_timestamp()
    {
        using var client = new HttpClient();
        var writer = InfluxV3WriterTests.Writer(client, server.Url);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var line = $"writerproof,empresa_id=company,veiculo_id=vehicle rpm=1i {timestamp}\n";
        var point = new TelemetryWritePoint(new BatchId(Guid.NewGuid()), 17,
            new EventIdentity(new CompanyId("company"), new VehicleId("vehicle"), EventTimestamp.FromUnixTimeMilliseconds(timestamp)));
        using var gzip = InfluxV3WriterTests.Gzip(line);
        var result = await writer.WriteAsync(new("0123456789abcdef0123456789abcdef", [point], Encoding.UTF8.GetByteCount(line)), gzip, CancellationToken.None);
        result.CommittedPoints.ShouldBe(1);
        result.Rejections.ShouldBeEmpty();
        var json = await client.GetStringAsync(server.Url + "/api/v3/query_sql?db=queryable&format=json&q=" + Uri.EscapeDataString("SELECT time FROM writerproof"));
        using var document = JsonDocument.Parse(json);
        DateTimeOffset.Parse(document.RootElement[0].GetProperty("time").GetString()!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).ToUnixTimeMilliseconds().ShouldBe(timestamp);
    }

    [Fact]
    public async Task Real_server_accepts_gzip_millisecond_write_and_reports_partial_line_number()
    {
        using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var line = $"telemetria,empresa_id=company,veiculo_id=vehicle rpm=1i {timestamp}\n";
        using var content = new StreamContent(InfluxV3WriterTests.Gzip(line + "invalid line\n"));
        content.Headers.ContentEncoding.Add("gzip");
        content.Headers.ContentType = new("text/plain");
        using var response = await client.PostAsync("/api/v3/write_lp?db=queryable&precision=millisecond&accept_partial=true&no_sync=false", content);
        ((int)response.StatusCode).ShouldBe(400);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data")[0].GetProperty("line_number").GetInt32().ShouldBe(2);
        var json = await client.GetStringAsync("/api/v3/query_sql?db=queryable&format=json&q=" + Uri.EscapeDataString("SELECT count(*) AS total FROM telemetria"));
        using var count = JsonDocument.Parse(json);
        count.RootElement[0].GetProperty("total").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Production_writer_reconciles_partial_acceptance_against_real_server()
    {
        using var client = new HttpClient();
        var writer = InfluxV3WriterTests.Writer(client, server.Url);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var batch = new BatchId(Guid.NewGuid());
        TelemetryWritePoint Point(int line, long time) => new(batch, line,
            new(new CompanyId("company"), new VehicleId("vehicle"), EventTimestamp.FromUnixTimeMilliseconds(time)));
        var seed = $"partialwriterproof,empresa_id=company,veiculo_id=vehicle rpm=1i {timestamp}\n";
        using (var gzip = InfluxV3WriterTests.Gzip(seed))
            (await writer.WriteAsync(new("0123456789abcdef0123456789abcdef", [Point(1, timestamp)], Encoding.UTF8.GetByteCount(seed)), gzip, default)).CommittedPoints.ShouldBe(1);
        var payload = $"partialwriterproof,empresa_id=company,veiculo_id=vehicle rpm=2i {timestamp + 1}\n" +
            $"partialwriterproof,empresa_id=company,veiculo_id=vehicle rpm=\"wrong type\" {timestamp + 2}\n";
        using var partial = InfluxV3WriterTests.Gzip(payload);
        var result = await writer.WriteAsync(new("0123456789abcdef0123456789abcdef",
            [Point(17, timestamp + 1), Point(29, timestamp + 2)], Encoding.UTF8.GetByteCount(payload)), partial, default);
        result.CommittedPoints.ShouldBe(1);
        var rejected = result.Rejections.ShouldHaveSingleItem();
        rejected.BatchId.ShouldBe(batch);
        rejected.LineNumber.ShouldBe(29);
        rejected.ErrorCode.ShouldBe(TelemetryErrorCodes.StorageSchemaConflict);
        using var document = JsonDocument.Parse(await client.GetStringAsync(server.Url + "/api/v3/query_sql?db=queryable&format=json&q=" + Uri.EscapeDataString("SELECT count(*) AS total FROM partialwriterproof")));
        document.RootElement[0].GetProperty("total").GetInt32().ShouldBe(2);
    }
}

public sealed class InfluxContractServer : IAsyncLifetime
{
    private IContainer? container;
    public string Url { get; private set; } = "";
    public async Task InitializeAsync()
    {
        container = new ContainerBuilder("influxdb:3.11.6-core")
            .WithCommand("influxdb3", "serve", "--node-id", "t038", "--object-store", "memory", "--without-auth")
            .WithPortBinding(8181, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8181).ForPath("/health")))
            .Build();
        await container.StartAsync();
        Url = $"http://{container.Hostname}:{container.GetMappedPublicPort(8181)}";
    }
    public async Task DisposeAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
    }
}
