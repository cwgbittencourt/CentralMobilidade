using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Api.Security;

public sealed class SecurityOptions
{
    public ProducerCredential[] Producers { get; set; } = [];
    public HumanOperatorCredential[] Operators { get; set; } = [];
    public ViewerPresentationServiceCredential[] ViewerServices { get; set; } = [];
    public OperatorAccessProfile[] Profiles { get; set; } = [];
}

public sealed class SecurityOptionsValidator : IValidateOptions<SecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, SecurityOptions options)
    {
        try
        {
            var credentials = options.Producers.Select(c => (c.CredentialId, c.Hash))
                .Concat(options.Operators.Select(c => (c.CredentialId, c.Hash)))
                .Concat(options.ViewerServices.Select(c => (c.CredentialId, c.Hash))).ToArray();
            if (credentials.Any(c => !ValidId(c.CredentialId) || c.Hash is null || !c.Hash.IsValid())
                || credentials.Select(c => c.CredentialId).Distinct(StringComparer.Ordinal).Count() != credentials.Length
                || credentials.Select(c => c.Hash.LookupDigest).Distinct(StringComparer.Ordinal).Count() != credentials.Length
                || options.Profiles.Select(p => p.ActorId).Distinct(StringComparer.Ordinal).Count() != options.Profiles.Length)
            {
                return Invalid();
            }
            foreach (var profile in options.Profiles)
            {
                if (!ValidId(profile.ActorId) || !Enum.IsDefined(profile.Kind) || profile.Kind == OperatorActorKind.Unidentified
                    || profile.Permissions is null || profile.CompanyIds is null
                    || profile.Permissions.Any(p => !OperatorAuthorizationPolicies.PermissionNames.Contains(p))
                    || profile.CompanyIds.Any(id => id is null))
                {
                    return Invalid();
                }
                foreach (var id in profile.CompanyIds)
                {
                    _ = new CompanyId(id);
                }
                if (profile.Kind == OperatorActorKind.ViewerPresentationService
                    && (!ValidId(profile.GrafanaOrganizationId) || profile.Permissions.Any(p => p is not ("telemetry.read" or "driver.read"))))
                {
                    return Invalid();
                }
            }
            foreach (var producer in options.Producers)
            {
                if (!ValidId(producer.ProducerId))
                {
                    return Invalid();
                }
            }
            foreach (var credential in options.Operators)
            {
                if (!options.Profiles.Any(p => p.ActorId == credential.ActorId && p.Kind == OperatorActorKind.Human))
                {
                    return Invalid();
                }
            }
            foreach (var credential in options.ViewerServices)
            {
                if (!ValidId(credential.GrafanaOrganizationId)
                    || !options.Profiles.Any(p => p.ActorId == credential.ActorId && p.Kind == OperatorActorKind.ViewerPresentationService
                        && p.GrafanaOrganizationId == credential.GrafanaOrganizationId))
                {
                    return Invalid();
                }
            }
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException) { return Invalid(); }
        catch (NullReferenceException) { return Invalid(); }
    }
    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
    private static ValidateOptionsResult Invalid() => ValidateOptionsResult.Fail("Authentication configuration is invalid.");
}

public sealed class ConfiguredSecurityStore(IOptionsMonitor<SecurityOptions> options) : IAuthenticationCredentialStore
{
    public ValueTask<ProducerCredential?> FindProducerAsync(string lookupDigest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(options.CurrentValue.Producers.SingleOrDefault(c => c.Hash.LookupDigest == lookupDigest));
    }
    public ValueTask<OperatorBearerCredential?> FindOperatorAsync(string lookupDigest, BearerCredentialKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OperatorBearerCredential? result = kind switch
        {
            BearerCredentialKind.HumanOperator => options.CurrentValue.Operators.SingleOrDefault(c => c.Hash.LookupDigest == lookupDigest),
            BearerCredentialKind.ViewerPresentationService => options.CurrentValue.ViewerServices.SingleOrDefault(c => c.Hash.LookupDigest == lookupDigest),
            _ => null
        };
        return ValueTask.FromResult(result);
    }
    public ValueTask<OperatorAccessProfile?> GetProfileAsync(string actorId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(options.CurrentValue.Profiles.SingleOrDefault(p => p.ActorId == actorId));
    }
}
