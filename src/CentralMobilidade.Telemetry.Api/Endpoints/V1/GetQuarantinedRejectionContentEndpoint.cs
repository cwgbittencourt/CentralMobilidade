using System;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Batches;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
namespace CentralMobilidade.Telemetry.Api.Endpoints.V1;

public static class GetQuarantinedRejectionContentEndpoint
{
    public const string Route = "/api/v1/telemetry/batches/{batchId}/rejections/{quarantineId}/content";
    public static RouteHandlerBuilder MapGetQuarantinedRejectionContentEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Route, HandleAsync).WithName("getQuarantinedRejectionContent")
            .RequireAuthorization(OperatorAuthorizationPolicies.OperatorAuthenticated)
            .WithMetadata(new RawQuarantineAuditMetadata())
            .Produces(200, contentType: "text/plain").ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);

    private static async Task HandleAsync(HttpContext context, [FromServices] GetQuarantinedRejectionContentHandler handler,
        [FromServices] IAuditWriter audit, [FromServices] IClock clock)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var actor = SecurityClaims.GetActor(context.User)!; // OperatorAuthenticated validated the trusted principal.
        try
        {
            var validBatch = Guid.TryParseExact(context.Request.RouteValues["batchId"]?.ToString(), "D", out var batch) && batch != Guid.Empty;
            var validQuarantine = Guid.TryParseExact(context.Request.RouteValues["quarantineId"]?.ToString(), "D", out var quarantine) && quarantine != Guid.Empty;
            if (!validBatch || !validQuarantine)
            {
                var record = new AuditRecord(Guid.NewGuid(), actor, AuditAction.ReadQuarantineContent, AuditResourceCategory.Quarantine,
                    validQuarantine ? quarantine.ToString("D") : null, [], clock.UtcNow, AuditOutcome.Missing, context.TraceIdentifier);
                try
                {
                    var receipt = await audit.AppendAsync(record, context.RequestAborted);
                    if (receipt.EventId != record.EventId || receipt.CommittedAt == default || receipt.CommittedAt.Offset != TimeSpan.Zero)
                        throw new InvalidOperationException();
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
                catch { throw new ProviderOperationException(TelemetryErrorCodes.AuditUnavailable, ProviderFailureKind.Transient); }
                await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.ResourceNotFound);
                return;
            }
            await using var result = await handler.HandleAsync(new BatchId(batch), quarantine, BatchReadIdentity.ForOperator(actor), context.TraceIdentifier, context.RequestAborted);
            if (result.ErrorCode is { } code)
            {
                await SecurityProblemResponses.WriteAsync(context, code);
                return;
            }
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers.ContentDisposition = "attachment; filename=\"rejection-" + quarantine.ToString("D") + ".txt\"";
            // Keep the audited result alive for the entire bounded, cancellation-aware transfer.
            await result.Content!.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (ProviderOperationException error) when (!context.Response.HasStarted)
        {
            await SecurityProblemResponses.WriteAsync(context, error.ErrorCode == TelemetryErrorCodes.AuditUnavailable
                ? TelemetryErrorCodes.AuditUnavailable : TelemetryErrorCodes.InternalError);
        }
    }
}
