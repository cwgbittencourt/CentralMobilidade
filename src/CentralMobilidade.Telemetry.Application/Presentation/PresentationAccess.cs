using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Presentation;

public sealed record PresentationContext(OperatorActor Actor, string TraceId)
{
    public override string ToString() => nameof(PresentationContext);
}
public sealed record PresentationResult<T>(T? Value, string? ErrorCode)
{
    public override string ToString() => nameof(PresentationResult<T>);
}

public sealed class PresentationAccess(IOperatorAuthorizer authorizer, IAuthenticationCredentialStore credentials, IAuditWriter audit, IClock clock)
{
    public async ValueTask<bool> ConfirmAsync(PresentationContext context, CompanyId company, OperatorPermission permission, CancellationToken token, bool crossCompanyAudited = false)
    {
        var decision = await CheckAsync(context, company, permission, token);
        if (!decision.Allowed)
            return false;
        if (decision.CrossCompany && !crossCompanyAudited)
        {
            await RecordAsync(context, AuditAction.ReadAcrossCompanies, permission == OperatorPermission.DriverRead ? AuditResourceCategory.Driver : AuditResourceCategory.Telemetry, company, AuditOutcome.Allowed, token);
            return (await CheckAsync(context, company, permission, token)).Allowed;
        }
        return true;
    }
    public async ValueTask<(bool Allowed, bool CrossCompany)> CheckAsync(PresentationContext context, CompanyId company, OperatorPermission permission, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var actor = context.Actor;
            if (actor.Kind is not (OperatorActorKind.Human or OperatorActorKind.ViewerPresentationService))
                return (false, false);
            var profile = await credentials.GetProfileAsync(actor.ActorId, token);
            if (profile is null || !profile.Enabled || profile.ActorId != actor.ActorId || profile.Kind != actor.Kind
                || actor.Kind == OperatorActorKind.ViewerPresentationService && (string.IsNullOrWhiteSpace(actor.GrafanaOrganizationId) || actor.GrafanaOrganizationId != profile.GrafanaOrganizationId))
                return (false, false);
            var name = permission == OperatorPermission.DriverRead ? "driver.read" : "telemetry.read";
            if (!profile.Permissions.Contains(name, StringComparer.Ordinal))
                return (false, false);
            var cross = !profile.CompanyIds.Contains(company.Value, StringComparer.Ordinal);
            if (cross && (actor.Kind != OperatorActorKind.Human || !profile.Permissions.Contains("cross_company.read", StringComparer.Ordinal)
                || await authorizer.AuthorizeAsync(actor, OperatorPermission.CrossCompanyRead, [], token) != OperatorAuthorizationDecision.Allowed))
                return (false, false);
            return (await authorizer.AuthorizeAsync(actor, permission, [company], token) == OperatorAuthorizationDecision.Allowed, cross);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return (false, false); }
    }

    public async ValueTask RecordAsync(PresentationContext context, AuditAction action, AuditResourceCategory category, CompanyId? company, AuditOutcome outcome, CancellationToken token)
    {
        var actor = context.Actor.Kind == OperatorActorKind.Unidentified ? new OperatorActor("unauthenticated", OperatorActorKind.Unidentified) : context.Actor;
        var record = new AuditRecord(Guid.NewGuid(), actor, action, category, null, company is null ? [] : [company], clock.UtcNow, outcome, context.TraceId);
        try
        {
            var receipt = await audit.AppendAsync(record, token);
            if (receipt.EventId != record.EventId || receipt.CommittedAt == default || receipt.CommittedAt.Offset != TimeSpan.Zero)
                throw new InvalidOperationException();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { throw new ProviderOperationException(TelemetryErrorCodes.AuditUnavailable, ProviderFailureKind.Transient); }
    }
}

internal static class PresentationBounds
{
    internal static bool Id(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
    internal static bool Window(DateTimeOffset from, DateTimeOffset to, TimeSpan maximum) => from.Offset == TimeSpan.Zero && to.Offset == TimeSpan.Zero && to > from && to - from <= maximum;
    internal static CompanyId? Company(string? value)
    {
        if (!Id(value))
            return null;
        try
        {
            return new(value!);
        }
        catch (ArgumentException) { return null; }
    }
    internal static VehicleId? Vehicle(string? value)
    {
        if (!Id(value))
            return null;
        try
        {
            return new(value!);
        }
        catch (ArgumentException) { return null; }
    }
}
