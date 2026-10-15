using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Influx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using AccessProfile = CentralMobilidade.Telemetry.Application.Security.OperatorAccessProfile;
namespace CentralMobilidade.Telemetry.ContractTests.Cadastral;

[Trait("Category", "Contract")]
public sealed class CadastralAdapterContractTests
{
    private static readonly CompanyId Company = new("company-a");
    private static readonly DriverId Driver = new("driver-1");

    [Fact]
    public void Optional_driver_port_and_public_dto_cannot_be_a_company_eligibility_lookup()
    {
        var method = typeof(IDriverResolver).GetMethods().ShouldHaveSingleItem();
        method.Name.ShouldBe("ResolveAsync");
        method.GetParameters().Select(x => x.ParameterType).ShouldBe(new[] { typeof(CompanyId), typeof(DriverId), typeof(CancellationToken) });
        typeof(DriverIdentityPresentation).GetProperties().Select(x => x.Name).Order().ShouldBe(new[] { "CpfLast4", "DisplayName", "DriverId" });
        typeof(InfluxV3TelemetryWriter).Assembly.GetType("CentralMobilidade.Telemetry.Infrastructure.Cadastral.ICompanyVisibilityResolver").ShouldBeNull();
        typeof(InfluxV3TelemetryWriter).GetConstructors().SelectMany(x => x.GetParameters()).ShouldAllBe(x => x.ParameterType != typeof(IDriverResolver));
        new DriverIdentityPresentation(Driver, "PRIVATE_NAME", "3344").ToString().ShouldNotContain("PRIVATE_NAME");
    }

    [Theory]
    [InlineData("12345678901")]
    [InlineData("123")]
    [InlineData("abcd")]
    public void Public_driver_presentation_rejects_a_full_or_malformed_cpf_suffix(string value)
    {
        var presentation = new DriverPresentation("driver-1", "PRIVATE_NAME", value);
        Validator.TryValidateObject(presentation, new ValidationContext(presentation), [], true).ShouldBeFalse();
        presentation.ToString().ShouldNotContain(value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Absent_or_unavailable_registry_cannot_gate_valid_id_write_confirmation_or_scoped_visibility_authorization(bool unavailable)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InfluxDb:Queryable:Url"] = "https://influx.invalid",
            ["InfluxDb:Queryable:Database"] = "telemetry",
            ["InfluxDb:Queryable:Token"] = "PRIVATE_TOKEN",
            ["Cadastral:BaseUrl"] = unavailable ? "https://registry.invalid" : null,
            ["CompanyRegistry:Url"] = unavailable ? "https://registry.invalid" : null
        }).Build();
        var requests = new List<Uri>();
        using var transport = new Transport((request, token) =>
        {
            requests.Add(request.RequestUri!);
            request.RequestUri!.Host.ShouldBe("influx.invalid", "The optional registry must never be contacted for eligibility");
            request.RequestUri.Query.ShouldContain("db=telemetry");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var client = new HttpClient(transport);
        var writer = new InfluxV3TelemetryWriter(client, config);
        var poison = new PoisonEnrichment();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config).AddSingleton<IDriverResolver>(poison);
        services.AddTelemetrySecurity(config);
        services.Configure<SecurityOptions>(options => options.Profiles = [new AccessProfile("operator", OperatorActorKind.Human, ["telemetry.read"], ["not-in-registry-a", "not-in-registry-b"])]);
        await using var provider = services.BuildServiceProvider();
        var authorizer = provider.GetRequiredService<IOperatorAuthorizer>();
        foreach (var name in new[] { "not-in-registry-a", "not-in-registry-b" })
        {
            var company = new CompanyId(name);
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var text = $"telemetria,empresa_id={name},veiculo_id=vehicle latitude=1.0,longitude=2.0,velocidade=0.0,rpm=0i,ignicao=false {timestamp.ToUnixTimeMilliseconds()}";
            using var raw = new MemoryStream();
            using (var gzip = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(Encoding.UTF8.GetBytes(text));
            raw.Position = 0;
            var point = new TelemetryWritePoint(new(Guid.NewGuid()), 1, new(company, new("vehicle"), EventTimestamp.FromUnixTimeMilliseconds(timestamp.ToUnixTimeMilliseconds())));
            (await writer.WriteAsync(new(new string('a', 32), [point], Encoding.UTF8.GetByteCount(text)), raw, default)).CommittedPoints.ShouldBe(1);
            (await authorizer.AuthorizeAsync(new("operator", OperatorActorKind.Human), OperatorPermission.TelemetryRead, [company], default)).ShouldBe(OperatorAuthorizationDecision.Allowed);
        }
        (await authorizer.AuthorizeAsync(new("operator", OperatorActorKind.Human), OperatorPermission.TelemetryRead, [new("outside-scope")], default)).ShouldBe(OperatorAuthorizationDecision.Denied);
        requests.Count.ShouldBe(2);
        poison.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Provider_replacement_invalidates_cached_identity_without_changing_the_driver_port()
    {
        await using var test = new Fixture();
        test.Transport.Reply = (request, token) => Task.FromResult(Identity(request.RequestUri!.Host));
        IDriverResolver resolver = test.Adapter();
        (await resolver.ResolveAsync(Company, Driver, default)).ShouldNotBeNull().DisplayName.ShouldBe("cadastral-a.invalid");
        test.Configuration["DriverEnrichment:BaseUrl"] = "https://cadastral-b.invalid";
        test.Configuration.Reload();
        (await resolver.ResolveAsync(Company, Driver, default)).ShouldNotBeNull().DisplayName.ShouldBe("cadastral-b.invalid");
        test.Transport.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Missing_driver_is_optional_and_never_becomes_an_unknown_company_decision()
    {
        await using var test = new Fixture();
        test.Transport.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        (await test.Adapter().ResolveAsync(Company, Driver, default)).ShouldBeNull();
        test.Transport.Paths.ShouldHaveSingleItem().ShouldContain("/drivers/driver-1");
        test.Transport.Paths.ShouldAllBe(x => !x.Contains("/companies", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(0)]
    public async Task Unavailable_optional_enrichment_returns_no_identity_without_backend_diagnostics(int status)
    {
        await using var test = new Fixture();
        test.Transport.Reply = (_, _) => status == 0 ? throw new HttpRequestException("PRIVATE_BACKEND_DIAGNOSTIC")
            : Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("PRIVATE_BACKEND_DIAGNOSTIC") });
        (await test.Adapter().ResolveAsync(Company, Driver, default)).ShouldBeNull();
        test.Log.Text.ShouldNotContain("PRIVATE_BACKEND_DIAGNOSTIC");
    }

    [Theory]
    [InlineData("11122233344")]
    [InlineData("111.222.333-44")]
    public async Task Source_cpf_is_reduced_to_four_digits_and_diagnostics_do_not_disclose_identity(string cpf)
    {
        await using var test = new Fixture();
        test.Transport.Reply = (_, _) => Task.FromResult(Identity("PRIVATE_DRIVER_NAME", cpf));
        var result = (await test.Adapter().ResolveAsync(Company, Driver, default)).ShouldNotBeNull();
        result.DriverId.ShouldBe(Driver);
        result.DisplayName.ShouldBe("PRIVATE_DRIVER_NAME");
        result.CpfLast4.ShouldBe("3344");
        var json = JsonSerializer.Serialize(new DriverPresentation(result.DriverId.Value, result.DisplayName, result.CpfLast4));
        json.ShouldNotContain(cpf);
        json.ShouldNotContain("11122233344");
        result.ToString().ShouldNotContain("PRIVATE_DRIVER_NAME");
        test.Log.Text.ShouldNotContain("PRIVATE_DRIVER_NAME");
        test.Log.Text.ShouldNotContain(cpf);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("abcdefghijkl")]
    public async Task Invalid_cpf_data_cannot_become_a_long_or_unmasked_suffix(string cpf)
    {
        await using var test = new Fixture();
        test.Transport.Reply = (_, _) => Task.FromResult(Identity("Name", cpf));
        var result = await test.Adapter().ResolveAsync(Company, Driver, default);
        result?.CpfLast4.ShouldBeNull();
    }

    [Fact]
    public async Task Positive_cache_expires_and_refreshes_changed_cadastral_data()
    {
        await using var test = new Fixture();
        test.Transport.Reply = (_, _) => Task.FromResult(Identity("Version A"));
        var resolver = test.Adapter();
        (await resolver.ResolveAsync(Company, Driver, default)).ShouldNotBeNull().DisplayName.ShouldBe("Version A");
        test.Transport.Reply = (_, _) => Task.FromResult(Identity("Version B"));
        (await resolver.ResolveAsync(Company, Driver, default)).ShouldNotBeNull().DisplayName.ShouldBe("Version A");
        test.Transport.Calls.ShouldBe(1);
        test.Clock.UtcNow += TimeSpan.FromMinutes(2);
        (await resolver.ResolveAsync(Company, Driver, default)).ShouldNotBeNull().DisplayName.ShouldBe("Version B");
        test.Transport.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Cache_keys_include_company_and_source_requests_carry_tenant_scope()
    {
        await using var test = new Fixture();
        test.Transport.Reply = (request, _) => Task.FromResult(Identity(request.RequestUri!.Query.Contains("company-a", StringComparison.Ordinal) ? "Tenant A" : "Tenant B"));
        var resolver = test.Adapter();
        (await resolver.ResolveAsync(Company, Driver, default)).ShouldNotBeNull().DisplayName.ShouldBe("Tenant A");
        (await resolver.ResolveAsync(new("company-b"), Driver, default)).ShouldNotBeNull().DisplayName.ShouldBe("Tenant B");
        test.Transport.Calls.ShouldBe(2);
        test.Transport.Paths.ShouldAllBe(x => x.Contains("empresaId=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_source_identity_for_another_driver_is_never_disclosed()
    {
        await using var test = new Fixture();
        test.Transport.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { driverId = "another-driver", displayName = "PRIVATE_WRONG_IDENTITY", cpf = "11122233344" })
        });
        (await test.Adapter().ResolveAsync(Company, Driver, default)).ShouldBeNull();
        test.Log.Text.ShouldNotContain("PRIVATE_WRONG_IDENTITY");
    }

    [Fact]
    public async Task Timeout_is_bounded_and_is_not_cached_as_an_identity()
    {
        await using var test = new Fixture();
        test.Transport.Reply = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Identity("Unexpected"); };
        var resolver = test.Adapter();
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        (await resolver.ResolveAsync(Company, Driver, deadline.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(4))).ShouldBeNull();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        test.Transport.Reply = (_, _) => Task.FromResult(Identity("Recovered"));
        (await resolver.ResolveAsync(Company, Driver, default)).ShouldNotBeNull().DisplayName.ShouldBe("Recovered");
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_without_a_source_request()
    {
        await using var test = new Fixture();
        var resolver = test.Adapter();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => resolver.ResolveAsync(Company, Driver, cancelled.Token).AsTask());
        test.Transport.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_or_absent_enrichment_requires_no_registry_connection(bool absent)
    {
        await using var test = new Fixture(configured: !absent);
        if (!absent)
            test.Configuration["DriverEnrichment:Enabled"] = "false";
        (await test.Adapter().ResolveAsync(Company, Driver, default)).ShouldBeNull();
        test.Transport.Calls.ShouldBe(0);
    }

    private static HttpResponseMessage Identity(string name, string? cpf = null) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            driverId = Driver.Value,
            displayName = name,
            cpf
        })
    };

    private sealed class Fixture : IAsyncDisposable
    {
        internal IConfigurationRoot Configuration
        {
            get;
        }
        internal Clock Clock { get; } = new();
        internal CaptureLog Log { get; } = new();
        internal Transport Transport { get; } = new((_, _) => Task.FromResult(Identity("Driver")));
        private readonly ServiceProvider services;
        private object? adapter;
        internal Fixture(bool configured = true)
        {
            var values = new Dictionary<string, string?> { ["Cadastral:BaseUrl"] = "https://registry.invalid", ["CompanyRegistry:Url"] = "https://registry.invalid" };
            if (configured)
            {
                values["DriverEnrichment:Enabled"] = "true";
                values["DriverEnrichment:BaseUrl"] = "https://cadastral-a.invalid";
                values["DriverEnrichment:Timeout"] = "00:00:00.100";
                values["DriverEnrichment:CacheTtl"] = "00:01:00";
            }
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var collection = new ServiceCollection().AddSingleton<IConfiguration>(Configuration).AddSingleton<IClock>(Clock).AddSingleton<TimeProvider>(Clock);
            collection.AddLogging(builder => builder.AddProvider(Log));
            collection.AddHttpClient().ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => Transport));
            collection.AddSingleton(new HttpClient(Transport, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan });
            services = collection.BuildServiceProvider();
        }
        internal IDriverResolver Adapter()
        {
            if (adapter is IDriverResolver existing)
                return existing;
            var type = typeof(InfluxV3TelemetryWriter).Assembly.GetType("CentralMobilidade.Telemetry.Infrastructure.Cadastral.ConfiguredCadastralAdapter");
            type.ShouldNotBeNull("T067 must implement the optional configured driver enrichment adapter; this active TDD test does not substitute a fake resolver");
            typeof(IDriverResolver).IsAssignableFrom(type).ShouldBeTrue();
            adapter = ActivatorUtilities.GetServiceOrCreateInstance(services, type);
            return (IDriverResolver)adapter;
        }
        public async ValueTask DisposeAsync()
        {
            if (adapter is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (adapter is IDisposable disposable)
                disposable.Dispose();
            await services.DisposeAsync();
            Transport.Dispose();
        }
    }
    private sealed class Clock : TimeProvider, IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } = reply;
        internal int Calls;
        internal List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Paths.Add(request.RequestUri!.AbsoluteUri);
            return Reply(request, token);
        }
    }
    private sealed class PoisonEnrichment : IDriverResolver
    {
        internal int Calls;
        public ValueTask<DriverIdentityPresentation?> ResolveAsync(CompanyId company, DriverId driver, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Optional enrichment must never gate persistence or visibility authorization");
        }
    }
    private sealed class CaptureLog : ILoggerProvider, ILogger
    {
        private readonly List<string> messages = [];
        internal string Text => string.Join('\n', messages);
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, error));
            if (error is not null)
                messages.Add(error.ToString());
        }
        public void Dispose()
        {
        }
    }
}
