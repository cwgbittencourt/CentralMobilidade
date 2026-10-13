using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface ICompanyVisibilityResolver
{
    // Unknown, unavailable, or inconclusive results route to Pending. Never infer visibility from ingestion.
    ValueTask<CompanyVisibility> ResolveAsync(CompanyId companyId, CancellationToken cancellationToken);
}
public enum CompanyVisibility
{
    Visible, Unknown, Unavailable, Inconclusive
}

public interface IDriverResolver
{
    // Tenant-scoped; never return a full CPF. Disclosure still requires authorization and durable audit.
    ValueTask<DriverIdentityPresentation?> ResolveAsync(CompanyId companyId, DriverId driverId, CancellationToken cancellationToken);
}
public sealed record DriverIdentityPresentation(DriverId DriverId, string DisplayName, string? CpfLast4)
{
    public override string ToString() => "[DriverIdentityPresentation]";
}
