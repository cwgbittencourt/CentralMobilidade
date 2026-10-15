using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Infrastructure.Influx;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Queries;

public sealed class VehicleTripQueryTests(HistoricalTripServer server) : IClassFixture<HistoricalTripServer>
{
    [Fact]
    public async Task Old_trip_reads_persisted_Parquet_in_event_time_order_with_half_open_bounds_and_scope()
    {
        server.ParquetFiles.ShouldBeGreaterThanOrEqualTo(433);
        using var json = await server.QueryAsync(server.TripSql(server.Start, server.Start.AddMinutes(20), 10000));
        AssertTrip(json.RootElement.EnumerateArray().Select(r => HistoricalTripServer.Time(r.GetProperty("time").GetString()!)).ToArray(), 4);
        json.RootElement.EnumerateArray().Select(r => r.GetProperty("latitude").GetDouble()).ShouldBe(new[] { 1d, 2d, 3d, 4d });
        foreach (var row in json.RootElement.EnumerateArray())
        {
            row.GetProperty("empresa_id").GetString().ShouldBe("unregistered-company");
            row.GetProperty("veiculo_id").GetString().ShouldBe("trip-vehicle");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Storage_orders_before_applying_the_point_limit(int limit)
    {
        using var json = await server.QueryAsync(server.TripSql(server.Start, server.Start.AddMinutes(20), limit));
        AssertTrip(json.RootElement.EnumerateArray().Select(r => HistoricalTripServer.Time(r.GetProperty("time").GetString()!)).ToArray(), limit);
    }

    [Fact]
    public async Task A_24_hour_window_is_queryable_even_when_data_is_older_than_72_hours()
    {
        using var json = await server.QueryAsync(server.EnvelopeSql(24));
        json.RootElement.GetArrayLength().ShouldBe(144);
        HistoricalTripServer.Time(json.RootElement[0].GetProperty("time").GetString()!).ShouldBe(server.Start);
        (DateTimeOffset.UtcNow - server.Start).ShouldBeGreaterThan(TimeSpan.FromDays(20));
    }

    [Fact]
    public async Task Sparse_windows_longer_than_72_hours_can_succeed_so_duration_is_not_a_provider_hard_limit()
    {
        // Only the trip table's few files are scanned, despite the wide historical interval.
        using var json = await server.QueryAsync(server.TripSql(server.Start, server.Start.AddDays(7), 10000));
        json.RootElement.GetArrayLength().ShouldBe(5);
    }

    [Fact]
    public async Task A_433_file_scan_exceeds_Core_default_envelope_instead_of_silently_returning_a_partial_trip()
    {
        using var response = await server.Client.GetAsync(server.QueryUrl(server.EnvelopeSql(73)));
        response.IsSuccessStatusCode.ShouldBeFalse();
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("432");
        body.ToLowerInvariant().ShouldContain("parquet");
        // A SQL LIMIT does not make an excessive file scan safe.
        using var limited = await server.Client.GetAsync(server.QueryUrl(server.EnvelopeSql(73) + " LIMIT 1"));
        limited.IsSuccessStatusCode.ShouldBeFalse();
        using var bounded = await server.QueryAsync(server.EnvelopeSql(1));
        bounded.RootElement.GetArrayLength().ShouldBe(6, "The file-limit error must not damage subsequent bounded queries");
    }

    [Theory]
    [Trait("PendingImplementation", "T069-T070")]
    [InlineData(10000, 4)]
    [InlineData(2, 2)]
    public async Task Production_trip_returns_old_single_vehicle_points_in_event_order(int limit, int expected)
    {
        await using var host = await TripHost.StartAsync(server);
        using var response = await host.GetAsync(server.Start, server.Start.AddMinutes(20), limit);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var points = JsonSerializer.Deserialize<TelemetryPosition[]>(await response.Content.ReadAsStringAsync()).ShouldNotBeNull();
        AssertTrip(points.Select(p => p.EventTime).ToArray(), expected);
        points.Select(p => p.Latitude).ShouldBe(Enumerable.Range(1, expected).Select(i => (double)i));
        foreach (var point in points)
        {
            point.EmpresaId.ShouldBe("unregistered-company");
            point.VehicleId.ShouldBe("trip-vehicle");
        }
    }

    [Fact]
    [Trait("PendingImplementation", "T069-T070")]
    public async Task Production_empty_old_window_is_an_empty_successful_array()
    {
        await using var host = await TripHost.StartAsync(server);
        using var response = await host.GetAsync(server.Start.AddHours(2), server.Start.AddHours(3), 100);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    [Trait("PendingImplementation", "T069-T070")]
    public async Task Production_enforces_24_hours_even_when_sparse_Core_query_would_succeed()
    {
        await using var host = await TripHost.StartAsync(server);
        using var response = await host.GetAsync(server.Start, server.Start.AddHours(24).AddTicks(1), 10000);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("REQUEST_INVALID");
    }

    private void AssertTrip(DateTimeOffset[] times, int count)
    {
        times.ShouldBe(Enumerable.Range(0, count).Select(i => server.Start.AddMinutes(i * 5)).ToArray());
    }

    private sealed class TripHost(WebApplication app, HttpClient client, string token) : IAsyncDisposable
    {
        public static async Task<TripHost> StartAsync(HistoricalTripServer server)
        {
            var add = Find("AddTelemetryPresentation", typeof(IServiceCollection), typeof(IConfiguration));
            var map = Find("MapPresentationEndpoints", typeof(IEndpointRouteBuilder));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var token = OpaqueBearerTokens.Generate(BearerCredentialKind.HumanOperator);
            var hash = BearerTokenHash.Create(token);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Operators:0:CredentialId"] = "trip-reader",
                ["Authentication:Operators:0:ActorId"] = "trip-human",
                ["Authentication:Operators:0:Hash:LookupDigest"] = hash.LookupDigest,
                ["Authentication:Operators:0:Hash:Salt"] = hash.Salt,
                ["Authentication:Operators:0:Hash:Verifier"] = hash.Verifier,
                ["Authentication:Operators:0:Hash:Iterations"] = hash.Iterations.ToString(CultureInfo.InvariantCulture),
                ["Authentication:Profiles:0:ActorId"] = "trip-human",
                ["Authentication:Profiles:0:Kind"] = "Human",
                ["Authentication:Profiles:0:Permissions:0"] = "telemetry.read",
                ["Authentication:Profiles:0:CompanyIds:0"] = "unregistered-company",
                ["InfluxDb:Queryable:Url"] = server.Url,
                ["InfluxDb:Queryable:Database"] = "queryable",
                ["InfluxDb:Queryable:Token"] = "test-only-token"
            });
            builder.Services.AddTelemetryOptions(builder.Configuration);
            builder.Services.AddTelemetrySecurity(builder.Configuration);
            builder.Services.AddSingleton<IAuditWriter, Audit>();
            builder.Services.AddSingleton(new HttpClient());
            builder.Services.AddHttpClient();
            add.Invoke(null, [builder.Services, builder.Configuration]);
            var app = builder.Build();
            try
            {
                app.UseAuthentication();
                app.UseAuthorization();
                map.Invoke(null, [app]);
                await app.StartAsync();
                return new(app, app.GetTestClient(), token);
            }
            catch { await app.DisposeAsync(); throw; }
        }

        private static MethodInfo Find(string name, params Type[] arguments)
        {
            var methods = new[] { typeof(SecurityRegistration).Assembly, typeof(TelemetryOptions).Assembly, typeof(InfluxV3TelemetryWriter).Assembly }
                .SelectMany(a => a.GetTypes()).SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.Name == name && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(arguments)).ToArray();
            methods.Length.ShouldBe(1, $"T069/T070 must provide production {name}; do not replace it with a test query handler.");
            return methods[0];
        }

        public async Task<HttpResponseMessage> GetAsync(DateTimeOffset from, DateTimeOffset to, int limit)
        {
            var path = "/api/v1/presentation/telemetry/trips/trip-vehicle?empresaId=unregistered-company&fromUtc=" + Uri.EscapeDataString(from.ToString("O"))
                + "&toUtc=" + Uri.EscapeDataString(to.ToString("O")) + "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
        // This task has no identity disclosure. Durable identity auditing remains T064/T068.
        private sealed class Audit : IAuditWriter
        {
            public ValueTask<AuditReceipt> AppendAsync(AuditRecord record, CancellationToken cancellationToken) => ValueTask.FromResult(new AuditReceipt(record.EventId, DateTimeOffset.UtcNow));
        }
    }
}

public sealed class HistoricalTripServer : IAsyncLifetime
{
    private IContainer container = null!;
    public HttpClient Client { get; private set; } = null!;
    public string Url { get; private set; } = "";
    public DateTimeOffset Start { get; } = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(-28), TimeSpan.Zero);
    public int ParquetFiles
    {
        get; private set;
    }

    public async Task InitializeAsync()
    {
        container = new ContainerBuilder("influxdb:3.11.6-core")
            .WithCommand("influxdb3", "serve", "--node-id", "t065", "--object-store", "file", "--data-dir", "/tmp/t065",
                "--without-auth", "--gen1-duration", "10m", "--wal-flush-interval", "100ms", "--wal-files-per-snapshot", "1", "--force-snapshot-max-age", "1s")
            .WithPortBinding(8181, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8181).ForPath("/health")))
            .Build();
        try
        {
            await container.StartAsync();
            Url = $"http://{container.Hostname}:{container.GetMappedPublicPort(8181)}";
            Client = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromSeconds(30) };
            // Arrival order deliberately differs from event order. The endpoint must not sort by arrival.
            var lines = new StringBuilder();
            foreach (var minute in new[] { 15, 0, 20, 10, 5 })
                lines.Append(Line("telemetria", "unregistered-company", "trip-vehicle", minute, 1 + minute / 5)).Append('\n');
            lines.Append(Line("telemetria", "unregistered-company", "different-vehicle", 5, 88)).Append('\n');
            lines.Append(Line("telemetria", "other-company", "trip-vehicle", 5, 89)).Append('\n');
            // A separate table gives a representative DEFAULT 432-file envelope, not a reduced test limit.
            for (var i = 432; i >= 0; i--)
                lines.Append(Line("query_envelope", "unregistered-company", "trip-vehicle", i * 10, 1)).Append('\n');
            using var content = new StringContent(lines.ToString(), Encoding.UTF8, "text/plain");
            using var response = await Client.PostAsync("/api/v3/write_lp?db=queryable&precision=millisecond&accept_partial=false&no_sync=false", content);
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(90))
            {
                using var files = await QueryAsync("SELECT table_name FROM system.parquet_files");
                ParquetFiles = files.RootElement.EnumerateArray().Count(f => f.GetProperty("table_name").GetString() == "query_envelope");
                var tripFiles = files.RootElement.EnumerateArray().Count(f => f.GetProperty("table_name").GetString() == "telemetria");
                if (ParquetFiles >= 433 && tripFiles >= 3)
                    return;
                await Task.Delay(500);
            }
            throw new TimeoutException($"T065 requires persisted Parquet before querying; observed envelope files={ParquetFiles}.");
        }
        catch { await DisposeAsync(); throw; }
    }

    private string Line(string table, string company, string vehicle, int minute, int latitude) =>
        $"{table},empresa_id={company},veiculo_id={vehicle} latitude={latitude}.0,longitude=2.0,velocidade=0.0,rpm=0i,ignicao=false {Start.AddMinutes(minute).ToUnixTimeMilliseconds()}";
    public string TripSql(DateTimeOffset from, DateTimeOffset to, int limit) =>
        $"SELECT time,empresa_id,veiculo_id,latitude FROM telemetria WHERE empresa_id='unregistered-company' AND veiculo_id='trip-vehicle' AND time >= '{from:O}' AND time < '{to:O}' ORDER BY time ASC LIMIT {limit}";
    public string EnvelopeSql(int hours) => $"SELECT time FROM query_envelope WHERE time >= '{Start:O}' AND time < '{Start.AddHours(hours):O}' ORDER BY time ASC";
    public string QueryUrl(string sql) => "/api/v3/query_sql?db=queryable&format=json&q=" + Uri.EscapeDataString(sql);
    public async Task<JsonDocument> QueryAsync(string sql)
    {
        using var response = await Client.GetAsync(QueryUrl(sql));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }
    public static DateTimeOffset Time(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (container is not null)
            await container.DisposeAsync();
    }
}
