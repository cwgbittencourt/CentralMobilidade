using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Api.Security;

public sealed class ProducerBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IAuthenticationCredentialStore store, IClock clock) : BearerHandlerBase(options, logger, encoder, store, clock)
{
    protected override async Task<ClaimsIdentity?> ResolveIdentityAsync(string token)
    {
        if (!OpaqueBearerTokens.HasValidFormat(token, BearerCredentialKind.Producer))
        {
            return null;
        }
        var credential = await Store.FindProducerAsync(BearerTokenHash.Lookup(token), Context.RequestAborted);
        if (credential is null || !IsActive(credential.Enabled, credential.ExpiresAt) || !credential.Hash.Matches(token))
        {
            return null;
        }
        return new ClaimsIdentity([new Claim(SecurityClaims.ProducerId, credential.ProducerId),
            new Claim(SecurityClaims.CredentialId, credential.CredentialId)], OperatorAuthorizationPolicies.ProducerScheme);
    }
}
