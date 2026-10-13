using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface IOperatorAuthorizer
{
    // Actor comes from the separate trusted operator credential store, never caller-supplied claims.
    // Resolve enabled state, permissions and allowed companies server-side. Producers cannot be operators.
    // Every required company must be permitted unless explicit cross_company.read allows the operation.
    ValueTask<OperatorAuthorizationDecision> AuthorizeAsync(OperatorActor actor, OperatorPermission permission,
        IReadOnlyCollection<CompanyId> requiredCompanies, CancellationToken cancellationToken);
}
public enum OperatorActorKind
{
    Human, ViewerPresentationService, Unidentified
}
public enum OperatorPermission
{
    BatchRead, TelemetryRead, DriverRead, QuarantineRawRead, CrossCompanyRead
}
public enum OperatorAuthorizationDecision
{
    Allowed, Denied
}
public sealed record OperatorActor(string ActorId, OperatorActorKind Kind, string? GrafanaOrganizationId = null);
