using System;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;

namespace CentralMobilidade.Telemetry.Application.Security;

public enum BearerCredentialKind
{
    Producer, HumanOperator, ViewerPresentationService
}
public sealed record ProducerCredential(string CredentialId, string ProducerId, BearerTokenHash Hash, bool Enabled = true, DateTimeOffset? ExpiresAt = null)
{
    public override string ToString() => "[ProducerCredential]";
}
public abstract record OperatorBearerCredential(string CredentialId, string ActorId, BearerTokenHash Hash, bool Enabled, DateTimeOffset? ExpiresAt);
public sealed record HumanOperatorCredential(string CredentialId, string ActorId, BearerTokenHash Hash, bool Enabled = true, DateTimeOffset? ExpiresAt = null)
    : OperatorBearerCredential(CredentialId, ActorId, Hash, Enabled, ExpiresAt)
{
    public override string ToString() => "[HumanOperatorCredential]";
}
public sealed record ViewerPresentationServiceCredential(string CredentialId, string ActorId, string GrafanaOrganizationId,
    BearerTokenHash Hash, bool Enabled = true, DateTimeOffset? ExpiresAt = null)
    : OperatorBearerCredential(CredentialId, ActorId, Hash, Enabled, ExpiresAt)
{
    public override string ToString() => "[ViewerPresentationServiceCredential]";
}
public sealed record OperatorAccessProfile(string ActorId, OperatorActorKind Kind, string[] Permissions, string[] CompanyIds,
    bool Enabled = true, string? GrafanaOrganizationId = null);
public interface IAuthenticationCredentialStore
{
    ValueTask<ProducerCredential?> FindProducerAsync(string lookupDigest, CancellationToken cancellationToken);
    ValueTask<OperatorBearerCredential?> FindOperatorAsync(string lookupDigest, BearerCredentialKind kind, CancellationToken cancellationToken);
    ValueTask<OperatorAccessProfile?> GetProfileAsync(string actorId, CancellationToken cancellationToken);
}
