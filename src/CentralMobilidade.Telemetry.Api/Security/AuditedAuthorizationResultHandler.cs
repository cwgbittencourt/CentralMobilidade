using System;
using System.Linq;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CentralMobilidade.Telemetry.Api.Security;

// Authentication denials on the raw endpoint must be audited before the challenge is returned.
public sealed record RawQuarantineAuditMetadata;

public sealed class AuditedAuthorizationResultHandler(IClock clock) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        var rawEndpoint = context.GetEndpoint()?.Metadata.GetMetadata<RawQuarantineAuditMetadata>() is not null;
        if (rawEndpoint)
            context.Response.Headers.CacheControl = "no-store";
        // Combined policies must retain the sensitive operation's audit category.
        var requirement = policy.Requirements.OfType<OperatorPermissionRequirement>()
            .OrderByDescending(item => item.Permission switch
            {
                OperatorPermission.QuarantineRawRead => 4,
                OperatorPermission.DriverRead => 3,
                OperatorPermission.CrossCompanyRead => 2,
                _ => 1
            }).FirstOrDefault();
        if (authorizeResult.Forbidden && policy.Requirements.OfType<ProducerBatchOwnershipRequirement>().Any())
        {
            await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.ResourceNotFound);
            return;
        }
        if (authorizeResult.Succeeded || requirement is null && !rawEndpoint)
        {
            await fallback.HandleAsync(next, context, policy, authorizeResult);
            return;
        }
        var (action, category) = rawEndpoint ? (AuditAction.ReadQuarantineContent, AuditResourceCategory.Quarantine) : requirement!.Permission switch
        {
            OperatorPermission.DriverRead => (AuditAction.ResolveDriver, AuditResourceCategory.Driver),
            OperatorPermission.QuarantineRawRead => (AuditAction.ReadQuarantineContent, AuditResourceCategory.Quarantine),
            OperatorPermission.BatchRead => (AuditAction.ReadBatch, AuditResourceCategory.Batch),
            OperatorPermission.CrossCompanyRead => (AuditAction.ReadAcrossCompanies, AuditResourceCategory.Telemetry),
            _ => (AuditAction.ReadTelemetry, AuditResourceCategory.Telemetry)
        };
        var actor = SecurityClaims.GetActor(context.User) ?? new OperatorActor("unauthenticated", OperatorActorKind.Unidentified);
        // Never echo driver, company, query, header, or raw content into the audit record.
        var rawResource = context.Request.RouteValues[category == AuditResourceCategory.Quarantine ? "quarantineId" : "batchId"]?.ToString();
        var opaqueResource = Guid.TryParseExact(rawResource, "D", out var resourceId) ? resourceId.ToString("D") : null;
        var record = new AuditRecord(Guid.NewGuid(), actor, action, category, opaqueResource, [], clock.UtcNow, AuditOutcome.Denied, context.TraceIdentifier);
        try
        {
            var writer = context.RequestServices.GetService<IAuditWriter>();
            if (writer is null)
            {
                throw new InvalidOperationException();
            }
            var receipt = await writer.AppendAsync(record, context.RequestAborted);
            if (receipt.EventId != record.EventId || receipt.CommittedAt == default || receipt.CommittedAt.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException();
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.AuditUnavailable);
            return;
        }
        if (authorizeResult.Forbidden && authorizeResult.AuthorizationFailure?.FailureReasons.Any(reason => reason.Message == OperatorPermissionHandler.ScopeDenied) == true)
        {
            await SecurityProblemResponses.WriteAsync(context, TelemetryErrorCodes.ResourceNotFound);
            return;
        }
        await fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
