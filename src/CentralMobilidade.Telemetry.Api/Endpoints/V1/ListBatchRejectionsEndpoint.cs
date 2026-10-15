using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Batches;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
namespace CentralMobilidade.Telemetry.Api.Endpoints.V1;

public static class ListBatchRejectionsEndpoint
{
    public const string Route = "/api/v1/telemetry/batches/{batchId}/rejections";
    public static RouteHandlerBuilder MapListBatchRejectionsEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Route, HandleAsync).WithName("listTelemetryBatchRejections")
            .RequireAuthorization(OperatorAuthorizationPolicies.OperatorAuthenticated)
            .Produces<RejectionPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);

    private static async Task HandleAsync(HttpContext context, [FromServices] ListBatchRejectionsHandler handler)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (SecurityClaims.GetActor(context.User) is not { Kind: OperatorActorKind.Human } actor)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.AuthenticationRequired);
            return;
        }
        if (!Guid.TryParseExact(context.Request.RouteValues["batchId"]?.ToString(), "D", out var id) || id == Guid.Empty)
        {
            await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.ResourceNotFound);
            return;
        }
        var limits = context.Request.Query["limit"];
        var cursors = context.Request.Query["cursor"];
        var limit = 50;
        if (limits.Count > 1 || limits.Count == 1 && (!int.TryParse(limits[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 200)
            || cursors.Count > 1 || cursors.Count == 1 && string.IsNullOrEmpty(cursors[0]))
        {
            await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.RequestInvalid);
            return;
        }
        try
        {
            var page = await handler.HandleAsync(new BatchId(id), BatchReadIdentity.ForOperator(actor), cursors.Count == 0 ? null : cursors[0], limit, context.RequestAborted);
            if (page is null)
            {
                await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.ResourceNotFound);
                return;
            }
            var body = new RejectionPage(page.Items.Select(x => new Rejection(x.LineNumber, x.ErrorCode!, x.SafeDigest,
                x.OccurredAt, x.CompanyId?.Value, x.VehicleId?.Value)).ToArray(), page.NextCursor);
            await context.Response.WriteAsJsonAsync(body, cancellationToken: context.RequestAborted);
        }
        catch (ProviderOperationException error)
        {
            await SecurityProblemResponses.WriteAsync(context, error.ErrorCode == TelemetryErrorCodes.RequestInvalid
                ? TelemetryErrorCodes.RequestInvalid : TelemetryErrorCodes.InternalError);
        }
    }
}
