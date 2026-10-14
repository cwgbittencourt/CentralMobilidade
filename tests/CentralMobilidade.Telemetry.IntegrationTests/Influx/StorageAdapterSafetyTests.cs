using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Cadastral;
using CentralMobilidade.Telemetry.Infrastructure.Influx;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Linq;
using Shouldly;
using Xunit;
using Microsoft.Extensions.Configuration;

namespace CentralMobilidade.Telemetry.IntegrationTests.Influx;

public sealed class StorageAdapterSafetyTests
{
    [Theory]
    [InlineData(200, "{\"companyId\":\"company\",\"visible\":true}", CompanyVisibility.Visible)]
    [InlineData(200, "{\"companyId\":\"company\",\"visible\":false}", CompanyVisibility.Unknown)]
    [InlineData(200, "{\"companyId\":\"other\",\"visible\":true}", CompanyVisibility.Inconclusive)]
    [InlineData(200, "{\"visible\":true}", CompanyVisibility.Inconclusive)]
    [InlineData(200, "{\"companyId\":\"company\",\"visible\":true,\"visible\":false}", CompanyVisibility.Inconclusive)]
    [InlineData(200, "not-json", CompanyVisibility.Inconclusive)]
    [InlineData(404, "", CompanyVisibility.Unknown)]
    [InlineData(503, "SEEDED_PRIVATE_PROVIDER_BODY", CompanyVisibility.Unavailable)]
    public async Task Visibility_requires_an_explicit_tenant_matching_positive_response(int status, string body, CompanyVisibility expected)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            request.RequestUri!.Query.ShouldBe("?companyId=company");
            request.Headers.Authorization!.ToString().ShouldBe("Bearer private-token");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        }));
        var adapter = new ConfiguredCadastralAdapter(client, Config());
        (await adapter.ResolveAsync(new CompanyId("company"), default)).ShouldBe(expected);
    }

    [Fact]
    public async Task Unconfigured_visibility_is_unavailable_without_network_access()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No network expected")));
        var adapter = new ConfiguredCadastralAdapter(client, new ConfigurationBuilder().Build());
        (await adapter.ResolveAsync(new("company"), default)).ShouldBe(CompanyVisibility.Unavailable);
    }

    [Fact]
    public async Task Lookup_timeout_is_unavailable_but_caller_cancellation_propagates()
    {
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var adapter = new ConfiguredCadastralAdapter(client, Config("00:00:00.05"));
        (await adapter.ResolveAsync(new("company"), default)).ShouldBe(CompanyVisibility.Unavailable);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => adapter.ResolveAsync(new("company"), cancelled.Token).AsTask());
    }

    [Fact]
    public async Task Unbounded_visibility_body_cannot_grant_visibility()
    {
        using var body = new GeneratedBody(1024 * 1024);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        (await new ConfiguredCadastralAdapter(client, Config()).ResolveAsync(new("company"), default)).ShouldBe(CompanyVisibility.Unavailable);
        body.ReadBytes.ShouldBeLessThanOrEqualTo(16385);
    }

    [Theory]
    [InlineData("{\"error\":\"partial write of line protocol occurred\",\"data\":[{\"line_number\":1,\"error_message\":\"invalid column type\"},{\"line_number\":1,\"error_message\":\"invalid column type\"}]}")]
    [InlineData("{\"error\":\"partial write of line protocol occurred\",\"data\":[{\"line_number\":1,\"line_number\":2,\"error_message\":\"invalid column type\"}]}")]
    [InlineData("{\"error\":\"not a confirmed partial outcome\",\"data\":[{\"line_number\":1,\"error_message\":\"invalid column type\"}]}")]
    public async Task Duplicate_or_unconfirmed_partial_results_are_ambiguous(string json)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(json) })));
        using var gzip = InfluxV3WriterTests.Gzip("a f=1i 1\n");
        var error = await Should.ThrowAsync<ProviderOperationException>(() => InfluxV3WriterTests.Writer(client, "http://localhost").WriteAsync(Request(), gzip, default).AsTask());
        error.Kind.ShouldBe(ProviderFailureKind.Ambiguous);
        error.InnerException.ShouldBeNull();
        gzip.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task Oversized_chunked_partial_response_is_bounded_and_never_committed()
    {
        using var body = new GeneratedBody(1024 * 1024);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(body) })));
        using var gzip = InfluxV3WriterTests.Gzip("a f=1i 1\n");
        var error = await Should.ThrowAsync<ProviderOperationException>(() => InfluxV3WriterTests.Writer(client, "http://localhost").WriteAsync(Request(), gzip, default).AsTask());
        error.Kind.ShouldBe(ProviderFailureKind.Ambiguous);
        body.ReadBytes.ShouldBeLessThanOrEqualTo(256 * 1024 + 1);
    }

    [Fact]
    public async Task Transport_failure_never_leaks_provider_details_or_retries_inside_writer()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            throw new HttpRequestException("SEEDED_PRIVATE_URL_TOKEN_BODY");
        }));
        using var gzip = InfluxV3WriterTests.Gzip("a f=1i 1\n");
        var error = await Should.ThrowAsync<ProviderOperationException>(() => InfluxV3WriterTests.Writer(client, "http://localhost").WriteAsync(Request(), gzip, default).AsTask());
        error.Kind.ShouldBe(ProviderFailureKind.Ambiguous);
        error.ToString().ShouldNotContain("SEEDED");
        calls.ShouldBe(1);
        gzip.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(429)]
    public async Task Unexpected_success_and_rate_limit_responses_cannot_confirm_persistence(int status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        using var gzip = InfluxV3WriterTests.Gzip("a f=1i 1\n");
        var error = await Should.ThrowAsync<ProviderOperationException>(() => InfluxV3WriterTests.Writer(client, "http://localhost").WriteAsync(Request(), gzip, default).AsTask());
        error.Kind.ShouldBe(ProviderFailureKind.Ambiguous);
    }

    [Fact]
    public async Task Limit_related_partial_response_maps_complete_rejection_without_committed_points()
    {
        const string json = "{\"error\":\"partial write of line protocol occurred\",\"data\":[{\"line_number\":1,\"error_message\":\"table limit exceeded\"}]}";
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent(json) })));
        using var gzip = InfluxV3WriterTests.Gzip("a f=1i 1\n");
        var result = await InfluxV3WriterTests.Writer(client, "http://localhost").WriteAsync(Request(), gzip, default);
        result.CommittedPoints.ShouldBe(0);
        result.Rejections.ShouldHaveSingleItem().LineNumber.ShouldBe(17);
    }

    [Fact]
    public async Task Writer_deadline_is_ambiguous_and_caller_cancellation_propagates()
    {
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var writer = new InfluxV3TelemetryWriter(client, WriterConfig(timeout: "00:00:00.05"));
        using var gzip = InfluxV3WriterTests.Gzip("a f=1i 1\n");
        var error = await Should.ThrowAsync<ProviderOperationException>(() => writer.WriteAsync(Request(), gzip, default).AsTask());
        error.Kind.ShouldBe(ProviderFailureKind.Ambiguous);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => writer.WriteAsync(Request(), gzip, cancelled.Token).AsTask());
        gzip.CanRead.ShouldBeTrue();
    }

    [Fact]
    public void Queryable_and_pending_cannot_share_the_same_storage_destination()
    {
        using var client = new HttpClient();
        Should.Throw<ArgumentException>(() => new InfluxV3TelemetryWriter(client, WriterConfig(pendingDatabase: "queryable")));
    }

    [Fact]
    public async Task Production_http_registration_does_not_follow_visibility_redirects()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var positiveCalls = 0;
        app.MapGet("/visibility", () => Microsoft.AspNetCore.Http.Results.Redirect("/positive"));
        app.MapGet("/positive", () => { positiveCalls++; return new { companyId = "company", visible = true }; });
        await app.StartAsync();
        try
        {
            var origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var configuration = new ConfigurationBuilder().AddConfiguration(Config()).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cadastral:CompanyVisibilityUrl"] = origin + "/visibility"
            }).Build();
            await using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddTelemetryStorageAdapters().BuildServiceProvider();
            var resolver = services.GetRequiredService<ICompanyVisibilityResolver>();
            (await resolver.ResolveAsync(new("company"), default)).ShouldBe(CompanyVisibility.Unavailable);
            positiveCalls.ShouldBe(0);
        }
        finally { await app.StopAsync(); }
    }

    private static IConfiguration WriterConfig(string timeout = "00:00:30", string pendingDatabase = "pending")
    {
        var values = new Dictionary<string, string?> { ["InfluxDb:WriteTimeout"] = timeout };
        foreach (var store in new[] { "Queryable", "Pending" })
        {
            values[$"InfluxDb:{store}:Url"] = "http://localhost";
            values[$"InfluxDb:{store}:Database"] = store == "Pending" ? pendingDatabase : "queryable";
            values[$"InfluxDb:{store}:Token"] = "test-only-token";
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static TelemetryWriteRequest Request() => new(TelemetryDestination.Queryable, "0123456789abcdef0123456789abcdef",
        [new(new BatchId(Guid.NewGuid()), 17, new(new("company"), new("vehicle"), EventTimestamp.FromUnixTimeMilliseconds(1)))], 10);
    private static IConfiguration Config(string timeout = "00:00:02") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Cadastral:CompanyVisibilityUrl"] = "http://localhost/visibility",
        ["Cadastral:Token"] = "private-token",
        ["Cadastral:LookupTimeout"] = timeout
    }).Build();
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class GeneratedBody(int size) : Stream
    {
        public int ReadBytes
        {
            get; private set;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => ReadBytes; set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, size - ReadBytes);
            buffer.AsSpan(offset, read).Fill((byte)'x');
            ReadBytes += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, size - ReadBytes);
            buffer.Span[..read].Fill((byte)'x');
            ReadBytes += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
