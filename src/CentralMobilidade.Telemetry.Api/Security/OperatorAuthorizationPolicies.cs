using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CentralMobilidade.Telemetry.Api.Security;

public static class OperatorAuthorizationPolicies
{
    public const string ProducerScheme = "ProducerBearer";
    public const string OperatorScheme = "OperatorBearer";
    public const string BatchStatusScheme = "BatchStatusBearer";
    public const string BatchStatusRead = "BatchStatusRead";
    public const string Producer = "Producer";
    public const string ProducerBatchRead = "ProducerBatchRead";
    public const string BatchRead = "batch.read";
    public const string TelemetryRead = "telemetry.read";
    public const string DriverRead = "driver.read";
    public const string QuarantineRawRead = "quarantine.raw.read";
    public const string CrossCompanyRead = "cross_company.read";
    public static IReadOnlySet<string> PermissionNames { get; } = new[] { BatchRead, TelemetryRead, DriverRead, QuarantineRawRead, CrossCompanyRead }.ToFrozenSet(StringComparer.Ordinal);
    public static string Name(OperatorPermission permission) => permission switch
    {
        OperatorPermission.BatchRead => BatchRead,
        OperatorPermission.TelemetryRead => TelemetryRead,
        OperatorPermission.DriverRead => DriverRead,
        OperatorPermission.QuarantineRawRead => QuarantineRawRead,
        OperatorPermission.CrossCompanyRead => CrossCompanyRead,
        _ => throw new ArgumentOutOfRangeException(nameof(permission))
    };
    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(BatchStatusRead, policy => policy.AddAuthenticationSchemes(BatchStatusScheme).RequireAuthenticatedUser());
        options.AddPolicy(Producer, policy => policy.AddAuthenticationSchemes(ProducerScheme).RequireAuthenticatedUser().RequireClaim(SecurityClaims.ProducerId));
        options.AddPolicy(ProducerBatchRead, policy => policy.AddAuthenticationSchemes(ProducerScheme).RequireAuthenticatedUser()
            .RequireClaim(SecurityClaims.ProducerId).AddRequirements(new ProducerBatchOwnershipRequirement()));
        foreach (var permission in Enum.GetValues<OperatorPermission>())
        {
            options.AddPolicy(Name(permission), policy => policy.AddAuthenticationSchemes(OperatorScheme)
                .RequireAuthenticatedUser().AddRequirements(new OperatorPermissionRequirement(permission)));
        }
        options.DefaultPolicy = new AuthorizationPolicyBuilder(OperatorScheme).RequireAuthenticatedUser().Build();
    }
}

public sealed record OperatorPermissionRequirement(OperatorPermission Permission) : IAuthorizationRequirement;

public sealed class StoredOperatorAuthorizer(IAuthenticationCredentialStore store) : IOperatorAuthorizer
{
    public async ValueTask<OperatorAuthorizationDecision> AuthorizeAsync(OperatorActor actor, OperatorPermission permission,
        IReadOnlyCollection<CompanyId> requiredCompanies, CancellationToken cancellationToken)
    {
        var profile = await store.GetProfileAsync(actor.ActorId, cancellationToken);
        if (profile is null || !profile.Enabled || profile.ActorId != actor.ActorId || profile.Kind != actor.Kind
            || actor.Kind is not (OperatorActorKind.Human or OperatorActorKind.ViewerPresentationService)
            || !profile.Permissions.Contains(OperatorAuthorizationPolicies.Name(permission), StringComparer.Ordinal))
        {
            return OperatorAuthorizationDecision.Denied;
        }
        if (actor.Kind == OperatorActorKind.ViewerPresentationService
            && (permission is not (OperatorPermission.TelemetryRead or OperatorPermission.DriverRead)
                || string.IsNullOrWhiteSpace(actor.GrafanaOrganizationId) || profile.GrafanaOrganizationId != actor.GrafanaOrganizationId))
        {
            return OperatorAuthorizationDecision.Denied;
        }
        var allCompaniesAllowed = requiredCompanies.All(company => profile.CompanyIds.Contains(company.Value, StringComparer.Ordinal));
        var crossCompany = actor.Kind == OperatorActorKind.Human && profile.Permissions.Contains(OperatorAuthorizationPolicies.CrossCompanyRead, StringComparer.Ordinal);
        return allCompaniesAllowed || crossCompany ? OperatorAuthorizationDecision.Allowed : OperatorAuthorizationDecision.Denied;
    }
}

public sealed class OperatorPermissionHandler(IOperatorAuthorizer authorizer) : AuthorizationHandler<OperatorPermissionRequirement>
{
    internal const string ScopeDenied = "Company scope unavailable.";
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OperatorPermissionRequirement requirement)
    {
        if (context.Resource is not HttpContext http || SecurityClaims.GetActor(context.User) is not { } actor)
        {
            context.Fail();
            return;
        }
        try
        {
            if (await authorizer.AuthorizeAsync(actor, requirement.Permission, [], http.RequestAborted) != OperatorAuthorizationDecision.Allowed)
            {
                context.Fail();
                return;
            }
            var scope = await ResolveScopeAsync(http, requirement.Permission);
            if ((!scope.Complete && await authorizer.AuthorizeAsync(actor, OperatorPermission.CrossCompanyRead, [], http.RequestAborted) != OperatorAuthorizationDecision.Allowed)
                || await authorizer.AuthorizeAsync(actor, requirement.Permission, scope.Companies, http.RequestAborted) != OperatorAuthorizationDecision.Allowed)
            {
                context.Fail(new AuthorizationFailureReason(this, ScopeDenied));
                return;
            }
            context.Succeed(requirement);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { context.Fail(); }
    }
    private static async Task<(IReadOnlyCollection<CompanyId> Companies, bool Complete)> ResolveScopeAsync(HttpContext http, OperatorPermission permission)
    {
        if (permission == OperatorPermission.CrossCompanyRead)
        {
            return ([], true);
        }
        if (http.Request.RouteValues.TryGetValue("batchId", out var rawBatchId))
        {
            var store = http.RequestServices.GetService<IBatchStateStore>();
            if (store is null)
            {
                return ([], false);
            }
            var batch = await store.GetAsync(BatchId.Parse(rawBatchId?.ToString() ?? ""), http.RequestAborted);
            return batch is null ? ([], false) : (batch.Companies, batch.CompanyScopeComplete);
        }
        var routeCompany = http.Request.RouteValues.TryGetValue("empresaId", out var rawCompany) ? rawCompany?.ToString() : null;
        var query = http.Request.Query["empresaId"];
        if (query.Count > 1 || (routeCompany is not null && query.Count == 1 && query[0] != routeCompany))
        {
            return ([], false);
        }
        var company = routeCompany ?? (query.Count == 1 ? query[0] : null);
        return string.IsNullOrWhiteSpace(company) ? ([], false) : ([new CompanyId(company)], true);
    }
}
