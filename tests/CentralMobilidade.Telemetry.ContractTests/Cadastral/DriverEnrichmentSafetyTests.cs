using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Cadastral;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.ContractTests.Cadastral;

public sealed class DriverEnrichmentSafetyTests
{
    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"driverId\":\"driver\",\"driverId\":\"other\",\"displayName\":\"private\"}")]
    [InlineData("{\"driverId\":\"driver\",\"displayName\":null}")]
    [InlineData("{\"driverId\":\"driver\",\"displayName\":\" \"}")]
    [InlineData("{\"driverId\":\"driver\",\"displayName\":\"a\\nname\"}")]
    [InlineData("{\"driverId\":\"driver\",\"displayName\":\"\\uD800\"}")]
    public async Task Malformed_or_ambiguous_identity_is_not_cached(string body)
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        (await fixture.ResolveAsync()).ShouldBeNull();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Identity("Recovered"));
        (await fixture.ResolveAsync()).ShouldNotBeNull().DisplayName.ShouldBe("Recovered");
        fixture.Handler.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Response_limit_applies_with_or_without_ContentLength(bool declared)
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = declared ? new StringContent(new string('x', 16 * 1024 + 1)) : new StreamContent(new NonSeekable(new byte[16 * 1024 + 1]))
        });
        (await fixture.ResolveAsync()).ShouldBeNull();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Identity("Recovered"));
        (await fixture.ResolveAsync()).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Timeout", "00:00:00")]
    [InlineData("Timeout", "00:00:31")]
    [InlineData("CacheTtl", "00:00:00")]
    [InlineData("CacheTtl", "00:05:01")]
    [InlineData("Enabled", "invalid")]
    [InlineData("BaseUrl", "file:///private")]
    [InlineData("BaseUrl", "https://user:password@source.invalid")]
    [InlineData("BaseUrl", "https://source.invalid?secret=private")]
    public async Task Invalid_optional_configuration_makes_no_source_request(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Config["DriverEnrichment:" + key] = value;
        (await fixture.ResolveAsync()).ShouldBeNull();
        fixture.Handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Reload_revokes_an_inflight_source_response_and_does_not_cache_it()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Reply = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Identity("Revoked source"); };
        var pending = fixture.ResolveAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Config["DriverEnrichment:BaseUrl"] = "https://new-source.invalid";
            fixture.Config.Reload();
        }
        finally { release.TrySetResult(); }
        (await pending).ShouldBeNull();
        fixture.Handler.Reply = (request, _) => Task.FromResult(Identity(request.RequestUri!.Host));
        (await fixture.ResolveAsync()).ShouldNotBeNull().DisplayName.ShouldBe("new-source.invalid");
    }

    [Fact]
    public async Task Disable_then_reenable_invalidates_previously_cached_identity()
    {
        using var fixture = new Fixture();
        (await fixture.ResolveAsync()).ShouldNotBeNull();
        fixture.Config["DriverEnrichment:Enabled"] = "false";
        fixture.Config.Reload();
        (await fixture.ResolveAsync()).ShouldBeNull();
        fixture.Config["DriverEnrichment:Enabled"] = "true";
        fixture.Config.Reload();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Identity("Changed"));
        (await fixture.ResolveAsync()).ShouldNotBeNull().DisplayName.ShouldBe("Changed");
        fixture.Handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Inflight_caller_cancellation_propagates_and_allows_later_recovery()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Reply = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Identity("Unexpected"); };
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Adapter.ResolveAsync(new("company"), new("driver"), cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        fixture.Handler.Reply = (_, _) => Task.FromResult(Identity("Recovered"));
        (await fixture.ResolveAsync()).ShouldNotBeNull().DisplayName.ShouldBe("Recovered");
    }

    [Fact]
    public async Task Cache_has_a_fixed_capacity_and_evicts_oldest_entries()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = (request, _) => Task.FromResult(Identity("Driver", Uri.UnescapeDataString(request.RequestUri!.Segments[^1])));
        for (var i = 0; i < 4097; i++)
            (await fixture.Adapter.ResolveAsync(new("company"), new("driver-" + i), default)).ShouldNotBeNull();
        fixture.Handler.Calls.ShouldBe(4097);
        (await fixture.Adapter.ResolveAsync(new("company"), new("driver-4096"), default)).ShouldNotBeNull();
        fixture.Handler.Calls.ShouldBe(4097);
        (await fixture.Adapter.ResolveAsync(new("company"), new("driver-0"), default)).ShouldNotBeNull();
        fixture.Handler.Calls.ShouldBe(4098);
    }

    private static HttpResponseMessage Identity(string name, string driver = "driver") => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            driverId = driver,
            displayName = name,
            cpf = "11122233344"
        })
    };
    private sealed class Fixture : IDisposable
    {
        public IConfigurationRoot Config
        {
            get;
        } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DriverEnrichment:Enabled"] = "true",
            ["DriverEnrichment:BaseUrl"] = "https://source.invalid",
            ["DriverEnrichment:Timeout"] = "00:00:02"
        }).Build();
        public Handler Handler { get; } = new();
        public ConfiguredCadastralAdapter Adapter
        {
            get;
        }
        public Fixture() => Adapter = new(new HttpClient(Handler), Config, new Clock());
        public ValueTask<DriverIdentityPresentation?> ResolveAsync() => Adapter.ResolveAsync(new("company"), new("driver"), default);
        public void Dispose()
        {
            Adapter.Dispose();
            (Config as IDisposable)?.Dispose();
        }
    }
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls
        {
            get; private set;
        }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } = (_, _) => Task.FromResult(Identity("Driver"));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Reply(request, token);
        }
    }
    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream input = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => input.ReadAsync(buffer, token);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                input.Dispose();
            base.Dispose(disposing);
        }
    }
}
