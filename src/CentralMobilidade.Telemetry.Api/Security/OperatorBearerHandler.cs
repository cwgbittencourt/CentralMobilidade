using System;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Api.Security;

public sealed class OperatorBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IAuthenticationCredentialStore store, IClock clock) : BearerHandlerBase(options, logger, encoder, store, clock)
{
    protected override async Task<ClaimsIdentity?> ResolveIdentityAsync(string token)
    {
        var kind = OpaqueBearerTokens.HasValidFormat(token, BearerCredentialKind.HumanOperator) ? BearerCredentialKind.HumanOperator
            : OpaqueBearerTokens.HasValidFormat(token, BearerCredentialKind.ViewerPresentationService) ? BearerCredentialKind.ViewerPresentationService
            : (BearerCredentialKind?)null;
        if (kind is null)
        {
            return null;
        }
        var credential = await Store.FindOperatorAsync(BearerTokenHash.Lookup(token), kind.Value, Context.RequestAborted);
        if ((kind == BearerCredentialKind.HumanOperator && credential is not HumanOperatorCredential)
            || (kind == BearerCredentialKind.ViewerPresentationService && credential is not ViewerPresentationServiceCredential))
        {
            return null;
        }
        if (credential is null || !IsActive(credential.Enabled, credential.ExpiresAt) || !credential.Hash.Matches(token))
        {
            return null;
        }
        var profile = await Store.GetProfileAsync(credential.ActorId, Context.RequestAborted);
        if (profile is null || !profile.Enabled || profile.ActorId != credential.ActorId)
        {
            return null;
        }
        var actorKind = credential is ViewerPresentationServiceCredential ? OperatorActorKind.ViewerPresentationService : OperatorActorKind.Human;
        if (profile.Kind != actorKind)
        {
            return null;
        }
        if (credential is ViewerPresentationServiceCredential service && service.GrafanaOrganizationId != profile.GrafanaOrganizationId)
        {
            return null;
        }
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, credential.ActorId),
            new Claim(SecurityClaims.ActorKind, actorKind.ToString()), new Claim(SecurityClaims.CredentialId, credential.CredentialId)],
            OperatorAuthorizationPolicies.OperatorScheme);
        if (actorKind == OperatorActorKind.ViewerPresentationService)
        {
            identity.AddClaim(new Claim(SecurityClaims.OrganizationId, profile.GrafanaOrganizationId!));
        }
        return identity;
    }
}

internal static class SecurityClaims
{
    internal const string ProducerId = "cm.producer_id";
    internal const string CredentialId = "cm.credential_id";
    internal const string ActorKind = "cm.actor_kind";
    internal const string OrganizationId = "cm.organization_id";
    internal static OperatorActor? GetActor(ClaimsPrincipal principal)
    {
        var identity = principal.Identity;
        var actorId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (identity?.IsAuthenticated != true || identity.AuthenticationType != OperatorAuthorizationPolicies.OperatorScheme
            || actorId is null || !Enum.TryParse<OperatorActorKind>(principal.FindFirstValue(ActorKind), out var kind)
            || kind is not (OperatorActorKind.Human or OperatorActorKind.ViewerPresentationService))
        {
            return null;
        }
        return new OperatorActor(actorId, kind, principal.FindFirstValue(OrganizationId));
    }
}
