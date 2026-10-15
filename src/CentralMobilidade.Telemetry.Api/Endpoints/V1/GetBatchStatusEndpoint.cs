using System;
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

public static class GetBatchStatusEndpoint
{
    public const string Route = "/api/v1/telemetry/batches/{batchId}";
    public static RouteHandlerBuilder MapGetBatchStatusEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Route, HandleAsync).WithName("getTelemetryBatch")
            .RequireAuthorization(OperatorAuthorizationPolicies.BatchStatusRead)
            .Produces<BatchStatus>().ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task HandleAsync(HttpContext context, [FromServices] GetBatchStatusHandler handler)
    {
        context.Response.Headers.CacheControl = "no-store";
        BatchReadIdentity identity;
        if (context.User.Identity?.AuthenticationType == OperatorAuthorizationPolicies.ProducerScheme
            && context.User.FindFirst(SecurityClaims.ProducerId)?.Value is { } producer)
            identity = BatchReadIdentity.ForProducer(producer);
        else if (SecurityClaims.GetActor(context.User) is { Kind: OperatorActorKind.Human } actor)
            identity = BatchReadIdentity.ForOperator(actor);
        else
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
        var result = await handler.HandleAsync(new BatchId(id), identity, context.RequestAborted);
        if (result is null)
        {
            await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.ResourceNotFound);
            return;
        }
        await context.Response.WriteAsJsonAsync(result, cancellationToken: context.RequestAborted);
    }
}
