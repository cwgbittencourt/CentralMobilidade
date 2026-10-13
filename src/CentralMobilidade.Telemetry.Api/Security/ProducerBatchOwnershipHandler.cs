using System;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CentralMobilidade.Telemetry.Api.Security;

public sealed record ProducerBatchOwnershipRequirement : IAuthorizationRequirement;

public sealed class ProducerBatchOwnershipHandler(IClock clock) : AuthorizationHandler<ProducerBatchOwnershipRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ProducerBatchOwnershipRequirement requirement)
    {
        if (context.Resource is not HttpContext http || context.User.Identity?.IsAuthenticated != true
            || context.User.Identity.AuthenticationType != OperatorAuthorizationPolicies.ProducerScheme)
        {
            context.Fail();
            return;
        }
        try
        {
            var store = http.RequestServices.GetService<IBatchStateStore>();
            var value = http.Request.RouteValues["batchId"]?.ToString();
            if (store is null || value is null)
            {
                context.Fail();
                return;
            }
            var batch = await store.GetAsync(BatchId.Parse(value), http.RequestAborted);
            if (batch is not null && batch.ExpiresAt > clock.UtcNow && batch.State != BatchLifecycle.Expired
                && batch.ProducerId == context.User.FindFirst(SecurityClaims.ProducerId)?.Value)
            {
                context.Succeed(requirement);
            }
            else
            {
                context.Fail();
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { context.Fail(); }
    }
}
