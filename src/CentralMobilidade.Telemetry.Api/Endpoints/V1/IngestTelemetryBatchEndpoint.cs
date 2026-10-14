using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Ingestion;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Infrastructure.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Api.Endpoints.V1;

public static class IngestTelemetryBatchEndpoint
{
    public const string Route = "/api/v1/telemetry/batches";
    private const int StatusRetryAfterSeconds = 1;

    public static IServiceCollection AddTelemetryIntakeEndpoint(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IntakeAdmissionGate>();
        return services;
    }

    public static RouteHandlerBuilder MapIngestTelemetryBatchEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapPost(Route, HandleAsync)
            .RequireAuthorization(OperatorAuthorizationPolicies.Producer);
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        AcceptTelemetryBatchHandler handler,
        IntakeAdmissionGate admission,
        IOptions<TelemetryOptions> configuredOptions,
        IClock clock)
    {
        if (!admission.TryEnter(out var lease))
            return Problem(context, TelemetryErrorCodes.IntakeBackpressure, admission.RetryAfterSeconds);
        using (lease)
        {
            if (!HasContractVersion(context.Request) || !HasGzipEncoding(context.Request) ||
                !HasUtf8PlainText(context.Request))
            {
                var code = !HasContractVersion(context.Request)
                    ? TelemetryErrorCodes.RequestInvalid
                    : TelemetryErrorCodes.UnsupportedMediaType;
                return Problem(context, code);
            }

            var producerId = context.User.FindFirst(SecurityClaims.ProducerId)?.Value;
            if (string.IsNullOrWhiteSpace(producerId))
                return Problem(context, TelemetryErrorCodes.AuthenticationRequired);

            var options = configuredOptions.Value;
            var spooler = new BoundedRequestSpool(options.MaxCompressedBytes, options.UploadTimeout);
            try
            {
                await using var spool = await spooler.ReadAsync(context.Request.Body,
                    context.Request.ContentLength, context.RequestAborted).ConfigureAwait(false);
                var now = clock.UtcNow.ToUniversalTime();
                var receivedAt = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
                var batchId = Guid.CreateVersion7(receivedAt);
                var traceId = Activity.Current?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString();
                var metadata = new TelemetryBatchEnvelope("1", batchId, traceId, producerId, receivedAt,
                    "text/plain; charset=utf-8", "gzip", spool.CompressedBytes, spool.PayloadSha256, "v1");
                var accepted = await handler.HandleAsync(new(metadata, spool.Body), context.RequestAborted)
                    .ConfigureAwait(false);
                var statusUrl = Route + "/" + accepted.BatchId;
                context.Response.Headers.Location = statusUrl;
                context.Response.Headers.RetryAfter = StatusRetryAfterSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new BatchAccepted(accepted.BatchId.Value, accepted.ReceivedAt, statusUrl),
                    statusCode: StatusCodes.Status202Accepted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (ProviderOperationException error)
            {
                var retryAfter = error.ErrorCode is TelemetryErrorCodes.DurabilityUnconfirmed or
                    TelemetryErrorCodes.IntakeBackpressure ? admission.RetryAfterSeconds : (int?)null;
                return Problem(context, error.ErrorCode, retryAfter);
            }
        }
    }

    private static bool HasContractVersion(HttpRequest request) =>
        request.Headers["X-Contract-Version"] is { Count: 1 } version &&
        StringComparer.Ordinal.Equals(version[0], "v1");

    private static bool HasGzipEncoding(HttpRequest request) =>
        request.Headers.ContentEncoding is { Count: 1 } encoding &&
        StringComparer.OrdinalIgnoreCase.Equals(encoding[0]?.Trim(), "gzip");

    private static bool HasUtf8PlainText(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !StringComparer.OrdinalIgnoreCase.Equals(contentType.MediaType, "text/plain"))
            return false;
        var parameters = contentType.Parameters.ToArray();
        return parameters.Length == 1 &&
            StringComparer.OrdinalIgnoreCase.Equals(parameters[0].Name, "charset") &&
            StringComparer.OrdinalIgnoreCase.Equals(parameters[0].Value?.Trim('"'), "utf-8");
    }

    private static IResult Problem(HttpContext context, string code, int? retryAfterSeconds = null)
    {
        var problem = TelemetryErrorCodes.GetProblem(code);
        if (retryAfterSeconds is not null)
            context.Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(new
        {
            code = problem.Code,
            type = problem.Type,
            title = problem.Title,
            status = problem.Status,
            detail = problem.Detail,
            traceId = context.TraceIdentifier
        }, statusCode: problem.Status, contentType: "application/problem+json");
    }
}

public sealed class IntakeAdmissionGate : IDisposable
{
    private readonly SemaphoreSlim slots;

    public IntakeAdmissionGate() : this(Math.Max(1, Environment.ProcessorCount), 1)
    {
    }

    public IntakeAdmissionGate(int capacity, int retryAfterSeconds)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (retryAfterSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(retryAfterSeconds));
        slots = new SemaphoreSlim(capacity, capacity);
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int RetryAfterSeconds
    {
        get;
    }

    public bool TryEnter(out IDisposable? lease)
    {
        if (!slots.Wait(0))
        {
            lease = null;
            return false;
        }
        lease = new AdmissionLease(slots);
        return true;
    }

    public void Dispose() => slots.Dispose();

    private sealed class AdmissionLease(SemaphoreSlim slots) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                slots.Release();
        }
    }
}
