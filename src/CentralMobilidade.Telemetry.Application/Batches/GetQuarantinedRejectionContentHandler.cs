using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
namespace CentralMobilidade.Telemetry.Application.Batches;

public sealed class QuarantinedContentResult(string? errorCode, IQuarantineContent? content) : IAsyncDisposable
{
    public string? ErrorCode { get; } = errorCode;
    public IQuarantineContent? Content { get; } = content;
    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
    public override string ToString() => nameof(QuarantinedContentResult);
}

public sealed class GetQuarantinedRejectionContentHandler(IBatchStateStore batches, IQuarantineMetadataQuery metadata,
    BatchVisibility visibility, IQuarantineStore quarantine, IAuditWriter audit, IClock clock)
{
    public async ValueTask<QuarantinedContentResult> HandleAsync(BatchId id, Guid quarantineId, BatchReadIdentity identity, string traceId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (quarantineId == Guid.Empty)
            throw new ArgumentException("An opaque identifier is required.", nameof(quarantineId));
        if (string.IsNullOrWhiteSpace(traceId) || traceId.Length > 128 || traceId.Any(char.IsControl))
            throw new ArgumentException("A safe trace identifier is required.", nameof(traceId));
        var actor = identity.Operator ?? new OperatorActor("unauthenticated", OperatorActorKind.Unidentified);
        async ValueTask Record(AuditOutcome outcome, BatchSnapshot? batch = null)
        {
            var record = new AuditRecord(Guid.NewGuid(), actor, AuditAction.ReadQuarantineContent, AuditResourceCategory.Quarantine,
                quarantineId.ToString("D"), batch?.Companies ?? [], clock.UtcNow, outcome, traceId);
            try
            {
                var receipt = await audit.AppendAsync(record, token);
                if (receipt.EventId != record.EventId || receipt.CommittedAt == default || receipt.CommittedAt.Offset != TimeSpan.Zero)
                    throw new InvalidOperationException();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { throw new ProviderOperationException(TelemetryErrorCodes.AuditUnavailable, ProviderFailureKind.Transient); }
        }
        async ValueTask<QuarantinedContentResult> Deny(AuditOutcome outcome, string code = TelemetryErrorCodes.ResourceNotFound)
        {
            await Record(outcome);
            return new(code, null);
        }
        if (identity.Operator is null)
            return await Deny(AuditOutcome.Denied, TelemetryErrorCodes.AuthenticationRequired);
        if (!await visibility.HasPermissionAsync(actor, OperatorPermission.QuarantineRawRead, token))
            return await Deny(AuditOutcome.Denied, TelemetryErrorCodes.AccessDenied);
        var batch = await batches.GetAsync(id, token);
        if (batch is null)
            return await Deny(AuditOutcome.Missing);
        if (!await visibility.CanReadAsync(batch, actor, OperatorPermission.QuarantineRawRead, token))
            return await Deny(AuditOutcome.Denied);
        if (visibility.IsExpired(batch))
            return await Deny(AuditOutcome.Expired);
        var stored = await metadata.FindAsync(id, quarantineId, token);
        if (stored is null)
            return await Deny(AuditOutcome.Missing);
        if (stored.DeletedAt is not null || stored.Metadata.DeleteAfter <= clock.UtcNow)
            return await Deny(AuditOutcome.Expired);
        QuarantineMetadata? file;
        try
        {
            file = await quarantine.GetMetadataAsync(quarantineId, token);
        }
        catch (ProviderOperationException) { return await Deny(AuditOutcome.Missing); }
        if (file is null || !Matches(file, stored.Metadata))
            return await Deny(AuditOutcome.Missing);
        await Record(AuditOutcome.Allowed, batch);
        if (visibility.IsExpired(batch) || file.DeleteAfter <= clock.UtcNow)
            return await Deny(AuditOutcome.Expired);
        IQuarantineContent? content;
        try
        {
            content = await quarantine.OpenReadAsync(quarantineId, token);
        }
        catch (ProviderOperationException) { return await Deny(AuditOutcome.Missing); }
        if (content is null)
            return await Deny(AuditOutcome.Missing);
        try
        {
            var current = await metadata.FindAsync(id, quarantineId, token);
            var parent = await batches.GetAsync(id, token);
            if (current is null || parent is null || content.ContentSha256 != stored.Metadata.ContentSha256)
            {
                await content.DisposeAsync();
                return await Deny(AuditOutcome.Missing);
            }
            if (current.DeletedAt is not null || current.Metadata.DeleteAfter <= clock.UtcNow || visibility.IsExpired(parent))
            {
                await content.DisposeAsync();
                return await Deny(AuditOutcome.Expired);
            }
            if (!await visibility.CanReadAsync(parent, actor, OperatorPermission.QuarantineRawRead, token))
            {
                await content.DisposeAsync();
                return await Deny(AuditOutcome.Denied);
            }
            return new(null, content);
        }
        catch { await content.DisposeAsync(); throw; }
    }
    private static bool Matches(QuarantineMetadata file, QuarantineMetadata stored) =>
        file.QuarantineId == stored.QuarantineId && file.BatchId == stored.BatchId && file.LineNumber == stored.LineNumber &&
        file.ErrorCode == stored.ErrorCode && file.ContentSha256 == stored.ContentSha256 &&
        file.CreatedAt.UtcTicks / 10 == stored.CreatedAt.UtcTicks / 10 && file.DeleteAfter.UtcTicks / 10 == stored.DeleteAfter.UtcTicks / 10;
}

