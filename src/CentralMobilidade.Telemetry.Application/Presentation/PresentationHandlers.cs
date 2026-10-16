using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Application.Presentation;

public sealed record LatestPositionPage(IReadOnlyList<TelemetryPosition> Items, string? NextCursor)
{
    public override string ToString() => nameof(LatestPositionPage);
}
public sealed class LatestPositionHandler(PresentationAccess access, ITelemetryPresentationReader reader, IOptionsMonitor<TelemetryOptions> options)
{
    public async ValueTask<PresentationResult<LatestPositionPage>> HandleAsync(PresentationContext context, string companyId, string? vehicleId, int? limit, string? cursor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var company = PresentationBounds.Company(companyId);
        var vehicle = vehicleId is null ? null : PresentationBounds.Vehicle(vehicleId);
        var ceiling = options.CurrentValue.Viewer.LatestPositionMaxVehicles;
        var count = limit ?? ceiling;
        string? after = null;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length is 0 or > 512)
                    throw new FormatException();
                var text = cursor.Replace('-', '+').Replace('_', '/');
                after = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '=')));
                if (PresentationBounds.Vehicle(after) is null)
                    throw new FormatException();
            }
            catch (Exception error) when (error is FormatException or ArgumentException) { return new(null, TelemetryErrorCodes.RequestInvalid); }
        }
        if (company is null || vehicleId is not null && vehicle is null || count < 1 || count > ceiling)
            return new(null, TelemetryErrorCodes.RequestInvalid);
        var decision = await access.CheckAsync(context, company, OperatorPermission.TelemetryRead, token);
        if (!decision.Allowed)
        {
            await access.RecordAsync(context, AuditAction.ReadTelemetry, AuditResourceCategory.Telemetry, null, AuditOutcome.Denied, token);
            return new(null, TelemetryErrorCodes.ResourceNotFound);
        }
        if (decision.CrossCompany)
            await access.RecordAsync(context, AuditAction.ReadAcrossCompanies, AuditResourceCategory.Telemetry, company, AuditOutcome.Allowed, token);
        var rows = await reader.LatestAsync(company, vehicle, after, count + 1, token);
        PresentationRows.Positions(rows, company.Value, vehicle?.Value, count + 1, after, null, null);
        if (!await access.ConfirmAsync(context, company, OperatorPermission.TelemetryRead, token, decision.CrossCompany))
        {
            await access.RecordAsync(context, AuditAction.ReadTelemetry, AuditResourceCategory.Telemetry, null, AuditOutcome.Denied, token);
            return new(null, TelemetryErrorCodes.ResourceNotFound);
        }
        var items = rows.Take(count).ToArray();
        var next = rows.Count > count ? Convert.ToBase64String(Encoding.UTF8.GetBytes(items[^1].VehicleId)).TrimEnd('=').Replace('+', '-').Replace('/', '_') : null;
        return new(new(items, next), null);
    }
}

public sealed class VehicleTripHandler(PresentationAccess access, ITelemetryPresentationReader reader, IOptionsMonitor<TelemetryOptions> options)
{
    public async ValueTask<PresentationResult<IReadOnlyList<TelemetryPosition>>> HandleAsync(PresentationContext context, string companyId, string vehicleId, DateTimeOffset from, DateTimeOffset to, int? limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var company = PresentationBounds.Company(companyId);
        var vehicle = PresentationBounds.Vehicle(vehicleId);
        var bounds = options.CurrentValue.Viewer;
        var count = limit ?? bounds.TripMaxPoints;
        if (company is null || vehicle is null || count < 1 || count > bounds.TripMaxPoints || !PresentationBounds.Window(from, to, bounds.TripMaxDuration))
            return new(null, TelemetryErrorCodes.RequestInvalid);
        var decision = await access.CheckAsync(context, company, OperatorPermission.TelemetryRead, token);
        if (!decision.Allowed)
        {
            await access.RecordAsync(context, AuditAction.ReadTelemetry, AuditResourceCategory.Telemetry, null, AuditOutcome.Denied, token);
            return new(null, TelemetryErrorCodes.ResourceNotFound);
        }
        if (decision.CrossCompany)
            await access.RecordAsync(context, AuditAction.ReadAcrossCompanies, AuditResourceCategory.Telemetry, company, AuditOutcome.Allowed, token);
        var rows = await reader.TripAsync(company, vehicle, from, to, count, token);
        PresentationRows.Positions(rows, company.Value, vehicle.Value, count, null, from, to);
        if (!await access.ConfirmAsync(context, company, OperatorPermission.TelemetryRead, token, decision.CrossCompany))
        {
            await access.RecordAsync(context, AuditAction.ReadTelemetry, AuditResourceCategory.Telemetry, null, AuditOutcome.Denied, token);
            return new(null, TelemetryErrorCodes.ResourceNotFound);
        }
        return new(rows, null);
    }
}

public sealed class IngestionHealthHandler(PresentationAccess access, IIngestionHealthReader reader, IOptionsMonitor<TelemetryOptions> options)
{
    public async ValueTask<PresentationResult<IReadOnlyList<IngestionHealthPoint>>> HandleAsync(PresentationContext context, string companyId, DateTimeOffset from, DateTimeOffset to, int? granularitySeconds, int? limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var company = PresentationBounds.Company(companyId);
        var bounds = options.CurrentValue.Viewer;
        var granularity = granularitySeconds ?? (int)Math.Ceiling(bounds.HealthMinGranularity.TotalSeconds);
        var count = limit ?? bounds.HealthMaxPoints;
        if (company is null || count < 1 || count > bounds.HealthMaxPoints || granularity < bounds.HealthMinGranularity.TotalSeconds
            || !PresentationBounds.Window(from, to, bounds.HealthMaxDuration))
            return new(null, TelemetryErrorCodes.RequestInvalid);
        var decision = await access.CheckAsync(context, company, OperatorPermission.TelemetryRead, token);
        if (!decision.Allowed)
        {
            await access.RecordAsync(context, AuditAction.ReadTelemetry, AuditResourceCategory.Telemetry, null, AuditOutcome.Denied, token);
            return new(null, TelemetryErrorCodes.ResourceNotFound);
        }
        if (decision.CrossCompany)
            await access.RecordAsync(context, AuditAction.ReadAcrossCompanies, AuditResourceCategory.Telemetry, company, AuditOutcome.Allowed, token);
        var rows = await reader.ReadAsync(company, from, to, granularity, count, token);
        if (rows.Count > count || rows.Any(row => row.EmpresaId != company.Value || row.Time.Offset != TimeSpan.Zero || row.Time < from || row.Time >= to || row.Accepted < 0 || row.Rejected < 0)
            || rows.Zip(rows.Skip(1)).Any(pair => pair.First.Time >= pair.Second.Time))
            throw PresentationRows.Failure();
        if (!await access.ConfirmAsync(context, company, OperatorPermission.TelemetryRead, token, decision.CrossCompany))
        {
            await access.RecordAsync(context, AuditAction.ReadTelemetry, AuditResourceCategory.Telemetry, null, AuditOutcome.Denied, token);
            return new(null, TelemetryErrorCodes.ResourceNotFound);
        }
        return new(rows, null);
    }
}

internal static class PresentationRows
{
    internal static void Positions(IReadOnlyList<TelemetryPosition> rows, string company, string? vehicle, int limit, string? after, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (rows.Count > limit)
            throw Failure();
        string? previous = after;
        DateTimeOffset? previousTime = null;
        foreach (var row in rows)
        {
            if (row.EmpresaId != company || vehicle is not null && row.VehicleId != vehicle || PresentationBounds.Vehicle(row.VehicleId) is null
                || row.EventTime.Offset != TimeSpan.Zero || !double.IsFinite(row.Latitude) || Math.Abs(row.Latitude) > 90
                || !double.IsFinite(row.Longitude) || Math.Abs(row.Longitude) > 180
                || row.Speed is { } speed && (!double.IsFinite(speed) || speed < 0)
                || from is not null && (row.EventTime < from || row.EventTime >= to || previousTime is not null && row.EventTime < previousTime)
                || from is null && previous is not null && Encoding.UTF8.GetBytes(previous).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(row.VehicleId)) >= 0)
                throw Failure();
            previous = row.VehicleId;
            previousTime = row.EventTime;
        }
    }
    internal static ProviderOperationException Failure() => new(TelemetryErrorCodes.InternalError, ProviderFailureKind.Transient);
}

public sealed class DriverPresentationHandler(PresentationAccess access, IDriverResolver resolver)
{
    public async ValueTask<PresentationResult<DriverPresentation>> HandleAsync(PresentationContext context, string companyId, string driverId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var company = PresentationBounds.Company(companyId);
        CentralMobilidade.Telemetry.Domain.ValueObjects.DriverId driver;
        try
        {
            if (!PresentationBounds.Id(driverId) || company is null)
                return new(null, TelemetryErrorCodes.RequestInvalid);
            driver = new(driverId);
        }
        catch (ArgumentException) { return new(null, TelemetryErrorCodes.RequestInvalid); }
        var decision = await access.CheckAsync(context, company, OperatorPermission.DriverRead, token);
        if (!decision.Allowed)
        {
            await access.RecordAsync(context, AuditAction.ResolveDriver, AuditResourceCategory.Driver, null, AuditOutcome.Denied, token);
            return new(null, TelemetryErrorCodes.AccessDenied);
        }
        if (decision.CrossCompany)
            await access.RecordAsync(context, AuditAction.ReadAcrossCompanies, AuditResourceCategory.Driver, company, AuditOutcome.Allowed, token);
        DriverIdentityPresentation? identity;
        try
        {
            identity = await resolver.ResolveAsync(company, driver, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { identity = null; }
        if (!await access.ConfirmAsync(context, company, OperatorPermission.DriverRead, token, decision.CrossCompany))
        {
            await access.RecordAsync(context, AuditAction.ResolveDriver, AuditResourceCategory.Driver, null, AuditOutcome.Denied, token);
            return new(null, TelemetryErrorCodes.AccessDenied);
        }
        var valid = identity is not null && identity.DriverId == driver && !string.IsNullOrWhiteSpace(identity.DisplayName)
            && identity.DisplayName.Length <= 256 && !identity.DisplayName.Any(char.IsControl)
            && (identity.CpfLast4 is null || identity.CpfLast4.Length == 4 && identity.CpfLast4.All(c => c is >= '0' and <= '9'));
        await access.RecordAsync(context, AuditAction.ResolveDriver, AuditResourceCategory.Driver, company, valid ? AuditOutcome.Allowed : AuditOutcome.Missing, token);
        // Revalidate again after the durable audit barrier, including revocation during commit.
        if (!await access.ConfirmAsync(context, company, OperatorPermission.DriverRead, token, decision.CrossCompany))
        {
            await access.RecordAsync(context, AuditAction.ResolveDriver, AuditResourceCategory.Driver, null, AuditOutcome.Denied, token);
            return new(null, TelemetryErrorCodes.AccessDenied);
        }
        return valid ? new(new(driver.Value, identity!.DisplayName, identity.CpfLast4), null) : new(null, TelemetryErrorCodes.ResourceNotFound);
    }
}
