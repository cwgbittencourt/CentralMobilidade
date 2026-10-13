using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CentralMobilidade.Telemetry.Infrastructure.Health;

// Implement on the queue adapter when its actual ingestion route can confirm durable publication.
// A TCP connection, management ping or successful publish to another route is insufficient.
public interface IDurablePublicationProbe
{
    ValueTask<DurablePublicationStatus> ProbeAsync(CancellationToken cancellationToken);
}
public sealed record DurablePublicationStatus(bool Connected, bool DurableRouteReady, bool ConfirmationsReady, bool Writable)
{
    public bool Ready => Connected && DurableRouteReady && ConfirmationsReady && Writable;
}

public sealed class HealthProbeOptions
{
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan WorkerMaxAge { get; set; } = TimeSpan.FromSeconds(30);
    public string? WorkerHealthUrl
    {
        get; set;
    }
    public void Validate()
    {
        if (ProbeTimeout < TimeSpan.FromMilliseconds(100) || ProbeTimeout > TimeSpan.FromSeconds(30)
            || WorkerMaxAge < TimeSpan.FromSeconds(1) || WorkerMaxAge > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("Health probe timeout or worker freshness interval is outside the permitted range.");
        if (WorkerHealthUrl is not null && !ValidOrigin(WorkerHealthUrl))
            throw new InvalidOperationException("Worker health URL must be an HTTP(S) origin without credentials.");
    }
    internal static bool ValidOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0
        && uri.Fragment.Length == 0 && uri.AbsolutePath == "/";
    internal CancellationTokenSource Deadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ProbeTimeout);
        return deadline;
    }
}

public sealed class WorkerProgress(TimeProvider clock)
{
    private long timestamp;
    private int signaled;
    // Signal from the active consumer/recovery loop, including its healthy idle polling path (T053).
    // Neither startup, a separate timer nor a health request is evidence of consumer progress.
    public void MarkHeartbeat()
    {
        Interlocked.Exchange(ref timestamp, clock.GetTimestamp());
        Volatile.Write(ref signaled, 1);
    }
    public void MarkUnavailable() => Volatile.Write(ref signaled, 0);
    public bool IsFresh(TimeSpan maxAge)
    {
        if (Volatile.Read(ref signaled) == 0)
            return false;
        var age = clock.GetElapsedTime(Interlocked.Read(ref timestamp), clock.GetTimestamp());
        return age >= TimeSpan.Zero && age <= maxAge;
    }
}

public sealed class WorkerFreshnessHealthCheck(WorkerProgress progress, HealthProbeOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(progress.IsFresh(options.WorkerMaxAge) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Worker progress unavailable."));
}

public sealed class DurablePublicationHealthCheck(IDurablePublicationProbe? probe = null, HealthProbeOptions? options = null) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (probe is null)
            return HealthCheckResult.Unhealthy("Durable publication unavailable.");
        using var deadline = (options ?? new()).Deadline(cancellationToken);
        try
        {
            var status = await probe.ProbeAsync(deadline.Token).AsTask().WaitAsync(deadline.Token);
            return status.Ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Durable publication unavailable.");
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("Durable publication unavailable."); }
    }
}

public sealed class PostgresHealthCheck(IDbContextFactory<TelemetryControlDbContext>? factory, HealthProbeOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (factory is null)
            return HealthCheckResult.Unhealthy("Control storage unavailable.");
        using var deadline = options.Deadline(cancellationToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(deadline.Token);
            // No writes, migrations or migration-owner permissions are required by a probe.
            var ready = await db.Database.SqlQueryRaw<bool>("""
                SELECT (current_setting('transaction_read_only') = 'off' AND NOT pg_is_in_recovery()
                    AND current_setting('fsync') = 'on' AND current_setting('synchronous_commit') <> 'off'
                    AND has_table_privilege(current_user, to_regclass('public.ingestion_batches'), 'SELECT')
                    AND has_table_privilege(current_user, to_regclass('public.ingestion_batches'), 'INSERT')
                    AND has_table_privilege(current_user, to_regclass('public.ingestion_batches'), 'UPDATE')
                    AND has_table_privilege(current_user, to_regclass('public.audit_events'), 'SELECT')
                    AND has_table_privilege(current_user, to_regclass('public.audit_events'), 'INSERT')
                    AND (SELECT count(*) = 8 FROM information_schema.tables
                        WHERE table_schema = 'public' AND table_name IN
                        ('ingestion_batches','line_processing_results','quarantined_rejections','event_identity_guards',
                         'event_identity_evidence','operator_access_profiles','pending_telemetry_promotions','audit_events')))
                    AS "Value"
                """).SingleAsync(deadline.Token);
            return ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Control storage unavailable.");
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("Control storage unavailable."); }
    }
}

public sealed class InfluxHealthCheck(HttpClient client, string? origin, string? token, HealthProbeOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (origin is null || !HealthProbeOptions.ValidOrigin(origin) || string.IsNullOrWhiteSpace(token))
            return HealthCheckResult.Unhealthy("Telemetry storage unavailable.");
        using var deadline = options.Deadline(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, origin.TrimEnd('/') + "/health");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).WaitAsync(deadline.Token);
            // Connectivity only: never insert telemetry or expose the dependency response body.
            return response.StatusCode == HttpStatusCode.OK ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Telemetry storage unavailable.");
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("Telemetry storage unavailable."); }
    }
}

public sealed class RemoteWorkerHealthCheck(HttpClient client, HealthProbeOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (options.WorkerHealthUrl is null)
            return HealthCheckResult.Unhealthy("Worker progress unavailable.");
        using var deadline = options.Deadline(cancellationToken);
        try
        {
            using var response = await client.GetAsync(options.WorkerHealthUrl.TrimEnd('/') + "/health/worker", HttpCompletionOption.ResponseHeadersRead, deadline.Token).WaitAsync(deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 256)
                return HealthCheckResult.Unhealthy("Worker progress unavailable.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).WaitAsync(deadline.Token);
            var bytes = new byte[257];
            var total = 0;
            while (total < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(total), deadline.Token).AsTask().WaitAsync(deadline.Token);
                if (read == 0)
                    break;
                total += read;
            }
            if (total > 256)
                return HealthCheckResult.Unhealthy("Worker progress unavailable.");
            using var body = JsonDocument.Parse(bytes.AsMemory(0, total));
            return body.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "healthy"
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Worker progress unavailable.");
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("Worker progress unavailable."); }
    }
}

public static class TelemetryHealthChecks
{
    public static IServiceCollection AddTelemetryHealthChecks(this IServiceCollection services, IConfiguration configuration, TelemetryServiceRole role)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        var options = new HealthProbeOptions();
        configuration.GetSection("Health").Bind(options, b => b.ErrorOnUnknownConfiguration = true);
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<WorkerProgress>();
        services.AddHttpClient("telemetry-health", c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = options.ProbeTimeout });
        var checks = services.AddHealthChecks();
        checks.AddCheck("live", () => HealthCheckResult.Healthy(), tags: ["live"]);
        checks.Add(new HealthCheckRegistration("postgres", sp => new PostgresHealthCheck(sp.GetService<IDbContextFactory<TelemetryControlDbContext>>(), options),
            HealthStatus.Unhealthy, ["ready", "dependency"], options.ProbeTimeout));
        checks.Add(new HealthCheckRegistration("publication", sp => new DurablePublicationHealthCheck(sp.GetService<IDurablePublicationProbe>(), options),
            HealthStatus.Unhealthy, role == TelemetryServiceRole.Api ? ["ready", "dependency"] : ["dependency"], options.ProbeTimeout));
        var stores = role == TelemetryServiceRole.Api ? new[] { "Queryable" } : ["Queryable", "Pending"];
        foreach (var store in stores)
        {
            var origin = configuration[$"InfluxDb:{store}:Url"];
            var token = configuration[$"InfluxDb:{store}:Token"];
            checks.Add(new HealthCheckRegistration("influx_" + store.ToLowerInvariant(), sp => new InfluxHealthCheck(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("telemetry-health"), origin, token, options),
                HealthStatus.Unhealthy, role == TelemetryServiceRole.Worker ? ["ready", "dependency"] : ["dependency"], options.ProbeTimeout));
        }
        checks.Add(new HealthCheckRegistration("worker", sp => role == TelemetryServiceRole.Worker
            ? new WorkerFreshnessHealthCheck(sp.GetRequiredService<WorkerProgress>(), options)
            : new RemoteWorkerHealthCheck(sp.GetRequiredService<IHttpClientFactory>().CreateClient("telemetry-health"), options),
            HealthStatus.Unhealthy, role == TelemetryServiceRole.Worker ? ["ready", "dependency", "worker"] : ["dependency"], options.ProbeTimeout));
        return services;
    }

    public static void MapTelemetryHealthEndpoints(this IEndpointRouteBuilder routes, bool worker = false)
    {
        Map(routes, "/health/live", "live");
        Map(routes, "/health/ready", "ready");
        Map(routes, "/health/dependencies", "dependency");
        if (worker)
            Map(routes, "/health/worker", "worker");
    }
    private static void Map(IEndpointRouteBuilder routes, string path, string tag)
    {
        routes.MapHealthChecks(path, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(tag),
            AllowCachingResponses = false,
            ResultStatusCodes = new Dictionary<HealthStatus, int> { [HealthStatus.Healthy] = 200, [HealthStatus.Degraded] = 503, [HealthStatus.Unhealthy] = 503 },
            ResponseWriter = (context, report) =>
            {
                context.Response.ContentType = "application/json";
                context.Response.Headers.CacheControl = "no-store";
                return context.Response.WriteAsync(report.Status == HealthStatus.Healthy ? "{\"status\":\"healthy\"}" : "{\"status\":\"unhealthy\"}", context.RequestAborted);
            }
        }).WithMetadata(new HttpMethodMetadata(["GET", "HEAD"])).AllowAnonymous();
    }
}
