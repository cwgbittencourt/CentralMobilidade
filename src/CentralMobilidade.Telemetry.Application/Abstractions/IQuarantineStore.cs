using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface IQuarantineStore
{
    // Atomic, protected, replay-safe by QuarantineId. Caller owns rawContent; never log it.
    ValueTask<QuarantineReceipt> WriteAsync(QuarantineWriteRequest request, Stream rawContent, CancellationToken cancellationToken);
    ValueTask<QuarantineMetadata?> GetMetadataAsync(Guid quarantineId, CancellationToken cancellationToken);
    // Called only after operator authorization and durable audit; no filesystem path is returned.
    ValueTask<IQuarantineContent?> OpenReadAsync(Guid quarantineId, CancellationToken cancellationToken);
    // Delete only if the stored deletion deadline is at or before cutoff; repeat calls are safe.
    ValueTask<bool> DeleteExpiredAsync(Guid quarantineId, DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public sealed record QuarantineWriteRequest(Guid QuarantineId, BatchId BatchId, int LineNumber, string ErrorCode,
    DateTimeOffset CreatedAt, DateTimeOffset DeleteAfter);
public sealed record QuarantineReceipt(Guid QuarantineId, string ContentSha256);
public sealed record QuarantineMetadata(Guid QuarantineId, BatchId BatchId, int LineNumber, string ErrorCode,
    DateTimeOffset CreatedAt, DateTimeOffset DeleteAfter, string ContentSha256);
public interface IQuarantineContent : IAsyncDisposable
{
    Stream Content
    {
        get;
    }
    string ContentSha256
    {
        get;
    }
}
