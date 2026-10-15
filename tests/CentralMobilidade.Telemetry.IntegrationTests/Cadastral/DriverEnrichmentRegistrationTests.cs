using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.Cadastral;
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

namespace CentralMobilidade.Telemetry.IntegrationTests.Cadastral;

public sealed class DriverEnrichmentRegistrationTests
{
    [Fact]
    public async Task Registered_resolver_preserves_cache_across_scopes_and_does_not_log_identity_or_HTTP_requests()
    {
        await using var host = await Host.StartAsync();
        var resolver = host.App.Services.GetRequiredService<IDriverResolver>();
        using (var scope = host.App.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IDriverResolver>().ShouldBeSameAs(resolver);
        for (var i = 0; i < 2; i++)
        {
            var result = (await resolver.ResolveAsync(new("company"), new("driver"), default)).ShouldNotBeNull();
            result.DisplayName.ShouldBe("PRIVATE_DRIVER_NAME");
            result.CpfLast4.ShouldBe("3344");
        }
        host.Calls.ShouldBe(1);
        var logs = string.Join('\n', host.Logs.Messages);
        logs.ShouldNotContain("PRIVATE_DRIVER_NAME");
        logs.ShouldNotContain("11122233344");
        logs.ShouldNotContain("System.Net.Http.HttpClient.DriverEnrichment");
        host.Configuration["DriverEnrichment:Enabled"] = "false";
        host.Configuration.Reload();
        (await resolver.ResolveAsync(new("company"), new("driver"), default)).ShouldBeNull();
        host.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Registered_HTTP_handler_neither_follows_redirects_nor_replays_source_cookies()
    {
        await using var host = await Host.StartAsync();
        host.Redirect = true;
        var resolver = host.App.Services.GetRequiredService<IDriverResolver>();
        for (var i = 0; i < 2; i++)
            (await resolver.ResolveAsync(new("company"), new("driver"), default)).ShouldBeNull();
        host.Calls.ShouldBe(2);
        host.RedirectCalls.ShouldBe(0);
        host.Cookies.ShouldAllBe(c => c.Length == 0);
    }

    [Fact]
    public async Task Source_path_and_company_query_preserve_escaped_identifiers_without_query_injection()
    {
        await using var host = await Host.StartAsync();
        var resolver = host.App.Services.GetRequiredService<IDriverResolver>();
        const string company = "company &?=#Á";
        const string driver = "driver ?#=漢";
        (await resolver.ResolveAsync(new(company), new(driver), default)).ShouldNotBeNull().DriverId.Value.ShouldBe(driver);
        host.Companies.ShouldBe(new[] { company });
        host.QueryFieldCounts.ShouldBe(new[] { 1 });
    }

    private sealed class Host(WebApplication app, IConfigurationRoot configuration, Logs logs) : IAsyncDisposable
    {
        public WebApplication App => app;
        public IConfigurationRoot Configuration => configuration;
        public Logs Logs => logs;
        public bool Redirect
        {
            get; set;
        }
        public int Calls
        {
            get; private set;
        }
        public int RedirectCalls
        {
            get; private set;
        }
        public List<string> Cookies { get; } = new();
        public List<string> Companies { get; } = new();
        public List<int> QueryFieldCounts { get; } = new();
        public static async Task<Host> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            var logs = new Logs();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["DriverEnrichment:Enabled"] = "true" });
            builder.Services.AddOptionalDriverEnrichment();
            var app = builder.Build();
            var host = new Host(app, builder.Configuration, logs);
            app.MapGet("/drivers/{driverId}", (HttpContext context, string driverId) =>
            {
                host.Calls++;
                host.Cookies.Add(context.Request.Headers.Cookie.ToString());
                host.Companies.Add(context.Request.Query["empresaId"].ToString());
                host.QueryFieldCounts.Add(context.Request.Query.Count);
                if (host.Redirect)
                {
                    context.Response.Headers.SetCookie = "private-cookie=value; Path=/";
                    return Results.Redirect("/redirect-target");
                }
                return Results.Json(new
                {
                    driverId,
                    displayName = "PRIVATE_DRIVER_NAME",
                    cpf = "11122233344"
                });
            });
            app.MapGet("/redirect-target", () => { host.RedirectCalls++; return Results.Json(new { driverId = "driver", displayName = "WRONG_SOURCE" }); });
            try
            {
                await app.StartAsync();
                builder.Configuration["DriverEnrichment:BaseUrl"] = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return host;
            }
            catch { await app.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
    private sealed class Logs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Messages);
        public void Dispose()
        {
        }
        private sealed class Capture(string category, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(category + ":" + formatter(state, exception));
        }
    }
}
