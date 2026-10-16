using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Presentation;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CentralMobilidade.Telemetry.Api.Endpoints.V1.Presentation;

public static class PresentationEndpoints
{
    public static IEndpointRouteBuilder MapPresentationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Configure(endpoints.MapGet("/api/v1/presentation/telemetry/latest", (Delegate)LatestAsync), "getLatestVehiclePositions", false)
            .Produces<TelemetryPosition[]>();
        Configure(endpoints.MapGet("/api/v1/presentation/telemetry/trips/{vehicleId}", (Delegate)TripAsync), "getVehicleTrip", false)
            .Produces<TelemetryPosition[]>();
        Configure(endpoints.MapGet("/api/v1/presentation/telemetry/ingestion-health", (Delegate)HealthAsync), "getCompanyIngestionHealth", false)
            .Produces<IngestionHealthPoint[]>();
        Configure(endpoints.MapGet("/api/v1/presentation/drivers/{driverId}", (Delegate)DriverAsync), "getDriverPresentation", true)
            .Produces<DriverPresentation>().ProducesProblem(403);
        return endpoints;
    }

    private static RouteHandlerBuilder Configure(RouteHandlerBuilder route, string name, bool driver) => route.WithName(name)
        .RequireAuthorization(OperatorAuthorizationPolicies.OperatorAuthenticated)
        .WithMetadata(new PresentationAuditMetadata(driver ? AuditAction.ResolveDriver : AuditAction.ReadTelemetry,
            driver ? AuditResourceCategory.Driver : AuditResourceCategory.Telemetry))
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(500).ProducesProblem(503);

    private static Task LatestAsync(HttpContext context) => ExecuteAsync(context, async identity =>
    {
        if (!Query(context, ["empresaId", "vehicleId", "limit", "cursor"]) || !Required(context, "empresaId", out var company)
            || !OptionalInteger(context, "limit", out var limit))
        {
            await InvalidAsync(context);
            return;
        }
        var handler = context.RequestServices.GetRequiredService<LatestPositionHandler>();
        var result = await handler.HandleAsync(identity, company, Optional(context, "vehicleId"), limit, Optional(context, "cursor"), context.RequestAborted);
        if (result.ErrorCode is { } code)
        {
            await SecurityProblemResponses.WriteAsync(context, code);
            return;
        }
        if (result.Value!.NextCursor is { } next)
            context.Response.Headers["X-Next-Cursor"] = next;
        await context.Response.WriteAsJsonAsync(result.Value.Items, cancellationToken: context.RequestAborted);
    });

    private static Task TripAsync(HttpContext context) => ExecuteAsync(context, async identity =>
    {
        if (!Query(context, ["empresaId", "fromUtc", "toUtc", "limit"]) || !Required(context, "empresaId", out var company)
            || !Window(context, out var from, out var to) || !OptionalInteger(context, "limit", out var limit))
        {
            await InvalidAsync(context);
            return;
        }
        var handler = context.RequestServices.GetRequiredService<VehicleTripHandler>();
        var result = await handler.HandleAsync(identity, company, context.Request.RouteValues["vehicleId"]?.ToString() ?? "", from, to, limit, context.RequestAborted);
        await WriteAsync(context, result);
    });

    private static Task HealthAsync(HttpContext context) => ExecuteAsync(context, async identity =>
    {
        if (!Query(context, ["empresaId", "fromUtc", "toUtc", "limit", "granularitySeconds"]) || !Required(context, "empresaId", out var company)
            || !Window(context, out var from, out var to) || !OptionalInteger(context, "limit", out var limit)
            || !OptionalInteger(context, "granularitySeconds", out var granularity))
        {
            await InvalidAsync(context);
            return;
        }
        var handler = context.RequestServices.GetRequiredService<IngestionHealthHandler>();
        var result = await handler.HandleAsync(identity, company, from, to, granularity, limit, context.RequestAborted);
        await WriteAsync(context, result);
    });

    private static Task DriverAsync(HttpContext context) => ExecuteAsync(context, async identity =>
    {
        if (!Query(context, ["empresaId"]) || !Required(context, "empresaId", out var company))
        {
            await InvalidAsync(context);
            return;
        }
        var handler = context.RequestServices.GetRequiredService<DriverPresentationHandler>();
        var result = await handler.HandleAsync(identity, company, context.Request.RouteValues["driverId"]?.ToString() ?? "", context.RequestAborted);
        await WriteAsync(context, result);
    });

    private static async Task ExecuteAsync(HttpContext context, Func<PresentationContext, Task> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var actor = SecurityClaims.GetActor(context.User);
        if (actor is null)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.AuthenticationRequired);
            return;
        }
        try
        {
            await action(new(actor, context.TraceIdentifier));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (ProviderOperationException error) when (!context.Response.HasStarted)
        {
            await SecurityProblemResponses.WriteAsync(context, error.ErrorCode == TelemetryErrorCodes.AuditUnavailable ? TelemetryErrorCodes.AuditUnavailable : TelemetryErrorCodes.InternalError);
        }
        catch (Exception) when (!context.Response.HasStarted)
        {
            await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.InternalError);
        }
    }

    private static async Task WriteAsync<T>(HttpContext context, PresentationResult<T> result)
    {
        if (result.ErrorCode is { } code)
            await SecurityProblemResponses.WriteAsync(context, code);
        else
            await context.Response.WriteAsJsonAsync(result.Value, cancellationToken: context.RequestAborted);
    }

    // Parse explicitly: framework binding errors must not bypass our stable, non-disclosing problem contract.
    private static bool Query(HttpContext context, string[] names) => context.Request.Query.All(pair =>
        names.Contains(pair.Key, StringComparer.Ordinal) && pair.Value.Count == 1 && !string.IsNullOrEmpty(pair.Value[0]));
    private static string? Optional(HttpContext context, string name) => context.Request.Query.TryGetValue(name, out var value) ? value[0] : null;
    private static bool Required(HttpContext context, string name, out string value)
    {
        value = Optional(context, name) ?? "";
        return value.Length > 0;
    }
    private static bool OptionalInteger(HttpContext context, string name, out int? value)
    {
        value = null;
        if (Optional(context, name) is not { } text)
            return true;
        if (text.Length > 10 || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;
        value = parsed;
        return true;
    }
    private static bool Window(HttpContext context, out DateTimeOffset from, out DateTimeOffset to)
    {
        from = default;
        to = default;
        return Timestamp(Optional(context, "fromUtc"), out from) && Timestamp(Optional(context, "toUtc"), out to);
    }
    private static bool Timestamp(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is { Length: <= 40 } && DateTimeOffset.TryParseExact(text,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value) && value.Offset == TimeSpan.Zero;
    }
    private static Task InvalidAsync(HttpContext context) => SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.RequestInvalid);
}
